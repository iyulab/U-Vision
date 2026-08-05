using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Auth;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 관리자 PIN 최초 설정(first-run bootstrap) 검증 — 신규 설치가 서버 파일 수정·재시작 없이 UI 만으로
/// PIN 을 정할 수 있어야 하며, 명시적 config PIN 설정 시엔 기존 동작과 byte-identical 이어야 한다.
///
/// 각 테스트는 격리된 DataPath 를 위해 자체 팩토리를 생성한다(IClassFixture 공유 시 admin-pin.json
/// 상태가 테스트 간 누출되므로).
/// </summary>
public sealed class AdminSetupTests
{
    private const string Api = "/api/u-vision";

    private static string TokenPath(UVisionApiFactory f) =>
        Path.Combine(f.DataPath, "admin-setup-token.txt");

    private static string PinFilePath(UVisionApiFactory f) =>
        Path.Combine(f.DataPath, "admin-pin.json");

    private static async Task<string> ReadTokenAsync(UVisionApiFactory f) =>
        (await File.ReadAllTextAsync(TokenPath(f))).Trim();

    // --- 부트스트랩 활성(config PIN 없음) ---------------------------------

    [Fact]
    public void Bootstrap_GeneratesSetupToken_WhenNoPin()
    {
        using var f = UVisionApiFactory.Create();
        _ = f.CreateClient(); // 호스트 시작 → HostedService.EnsureBootstrap 실행.
        Assert.True(File.Exists(TokenPath(f)));
        Assert.False(File.Exists(PinFilePath(f)));
    }

    [Fact]
    public async Task Status_SetupRequired_WhenNoPin()
    {
        using var f = UVisionApiFactory.Create();
        var doc = JsonDocument.Parse(await f.CreateClient().GetStringAsync($"{Api}/admin/status"));
        Assert.False(doc.RootElement.GetProperty("pin_configured").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("setup_required").GetBoolean());
    }

    [Fact]
    public async Task AdminMutation_503_WithSetupMarker_WhenNoPin()
    {
        using var f = UVisionApiFactory.Create();
        var resp = await f.CreateClient().PostAsJsonAsync($"{Api}/scenarios", new { name = "x" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("setup_required").GetBoolean());
    }

    /// <summary>Trap 2 — 최초 설정 직후 재시작 없이 곧바로 새 PIN 으로 관리 기능을 쓸 수 있어야 한다.</summary>
    [Fact]
    public async Task Setup_ThenCreateScenario_Succeeds_NoRestart()
    {
        using var f = UVisionApiFactory.Create();
        var client = f.CreateClient();
        var token = await ReadTokenAsync(f);

        var setup = await client.PostAsJsonAsync($"{Api}/admin/setup",
            new { setup_token = token, new_pin = "9999" });
        Assert.Equal(HttpStatusCode.NoContent, setup.StatusCode);

        // 토큰 폐기 + 해시 파일 기록.
        Assert.False(File.Exists(TokenPath(f)));
        Assert.True(File.Exists(PinFilePath(f)));

        // 재시작 없이 즉시 새 PIN 으로 시나리오 생성.
        var create = new HttpRequestMessage(HttpMethod.Post, $"{Api}/scenarios")
        {
            Content = JsonContent.Create(new { name = "부트스트랩 후 시나리오", criteria = "c" }),
        };
        create.Headers.Add("X-Admin-Pin", "9999");
        var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Setup_StoresHash_NotPlaintext()
    {
        using var f = UVisionApiFactory.Create();
        var client = f.CreateClient();
        var token = await ReadTokenAsync(f);
        await client.PostAsJsonAsync($"{Api}/admin/setup", new { setup_token = token, new_pin = "9999" });

        var content = await File.ReadAllTextAsync(PinFilePath(f));
        Assert.DoesNotContain("9999", content);      // 평문 PIN 이 새지 않는다.
        Assert.Contains("pbkdf2", content);           // PBKDF2 해시 형식.
    }

    [Fact]
    public async Task Setup_WrongToken_401()
    {
        using var f = UVisionApiFactory.Create();
        var client = f.CreateClient();
        _ = await ReadTokenAsync(f); // 부트스트랩 실행 보장.
        var resp = await client.PostAsJsonAsync($"{Api}/admin/setup",
            new { setup_token = "wrong-token", new_pin = "9999" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Setup_ShortPin_400()
    {
        using var f = UVisionApiFactory.Create();
        var client = f.CreateClient();
        var token = await ReadTokenAsync(f);
        var resp = await client.PostAsJsonAsync($"{Api}/admin/setup",
            new { setup_token = token, new_pin = "12" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Setup_Twice_409_AlreadyConfigured()
    {
        using var f = UVisionApiFactory.Create();
        var client = f.CreateClient();
        var token = await ReadTokenAsync(f);

        var first = await client.PostAsJsonAsync($"{Api}/admin/setup",
            new { setup_token = token, new_pin = "9999" });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // 두 번째: admin-pin.json 이 이미 존재 → 재설정 거부.
        var second = await client.PostAsJsonAsync($"{Api}/admin/setup",
            new { setup_token = token, new_pin = "8888" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // --- Trap 4: 명시적 config PIN 설정 시 부트스트랩 완전 비활성 -------------

    [Fact]
    public void ConfigPin_NoTokenGenerated()
    {
        using var f = new PinFactory();
        _ = f.CreateClient();
        Assert.False(File.Exists(Path.Combine(f.DataPath, "admin-setup-token.txt")));
    }

    [Fact]
    public async Task ConfigPin_Setup_410_Disabled()
    {
        using var f = new PinFactory();
        var resp = await f.CreateClient().PostAsJsonAsync($"{Api}/admin/setup",
            new { setup_token = "anything", new_pin = "9999" });
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
    }

    [Fact]
    public async Task ConfigPin_Status_Configured_NoSetupRequired()
    {
        using var f = new PinFactory();
        var doc = JsonDocument.Parse(await f.CreateClient().GetStringAsync($"{Api}/admin/status"));
        Assert.True(doc.RootElement.GetProperty("pin_configured").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("setup_required").GetBoolean());
    }

    // --- PBKDF2 해시 round-trip(유닛) -------------------------------------

    [Fact]
    public void Hash_RoundTrip_VerifiesCorrectPin_RejectsWrong()
    {
        var hash = AdminPinHashing.Create("secret-pin");
        Assert.True(AdminPinHashing.Verify("secret-pin", hash));
        Assert.False(AdminPinHashing.Verify("wrong-pin", hash));
        Assert.Equal("pbkdf2-sha256", hash.Algorithm);
    }
}
