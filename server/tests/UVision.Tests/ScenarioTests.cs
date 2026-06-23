using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Auth;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 시나리오 CRUD + 관리자 PIN 테스트(S-B).
/// PIN 이 설정된 팩토리에서 인증·CRUD 왕복을, 미설정 동작은 별도로 검증한다.
/// </summary>
public class ScenarioTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;

    public ScenarioTests(PinFactory factory) => _factory = factory;

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        return client;
    }

    private static HttpContent Body(string name, string criteria = "기준") =>
        JsonContent.Create(new { name, criteria });

    [Fact]
    public async Task Create_DerivesSlug_AndRoundTrips()
    {
        var resp = await Admin().PostAsync("/api/u-vision/scenarios", Body("PCB Top Inspect"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pcb-top-inspect", created.GetProperty("scenario_id").GetString());
        Assert.Equal("PCB Top Inspect", created.GetProperty("name").GetString());

        // GET 단건으로 왕복 확인(무인증).
        var get = await _factory.CreateClient().GetAsync("/api/u-vision/scenarios/pcb-top-inspect");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Create_KoreanName_FallsBackToScenarioSlug()
    {
        // 한글 전용 이름 → ASCII 추출 불가 → "scenario" fallback(+충돌 시 접미).
        var r1 = await Admin().PostAsync("/api/u-vision/scenarios", Body("상면 검사"));
        var s1 = (await r1.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("scenario_id").GetString();
        var r2 = await Admin().PostAsync("/api/u-vision/scenarios", Body("하면 검사"));
        var s2 = (await r2.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("scenario_id").GetString();

        Assert.StartsWith("scenario", s1);
        Assert.StartsWith("scenario", s2);
        Assert.NotEqual(s1, s2); // 충돌 회피
    }

    [Fact]
    public async Task Create_SameName_SuffixesOnCollision()
    {
        await Admin().PostAsync("/api/u-vision/scenarios", Body("Line A"));
        var resp = await Admin().PostAsync("/api/u-vision/scenarios", Body("Line A"));
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("scenario_id").GetString();
        Assert.Equal("line-a-2", id);
    }

    [Fact]
    public async Task Update_ChangesFields_KeepsId()
    {
        var created = await (await Admin().PostAsync("/api/u-vision/scenarios", Body("Up Test", "old")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("scenario_id").GetString();

        var put = await Admin().PutAsync($"/api/u-vision/scenarios/{id}",
            JsonContent.Create(new { name = "Up Test", criteria = "new" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, updated.GetProperty("scenario_id").GetString());
        Assert.Equal("new", updated.GetProperty("criteria").GetString());
    }

    [Fact]
    public async Task Delete_RemovesScenario()
    {
        var created = await (await Admin().PostAsync("/api/u-vision/scenarios", Body("Del Test")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("scenario_id").GetString();

        var del = await Admin().DeleteAsync($"/api/u-vision/scenarios/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await _factory.CreateClient().GetAsync($"/api/u-vision/scenarios/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Update_Unknown_Returns404()
    {
        var put = await Admin().PutAsync("/api/u-vision/scenarios/ghost",
            JsonContent.Create(new { name = "x" }));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Mutation_WithoutPin_Returns401()
    {
        // PIN 헤더 없는 무인증 클라이언트로 변경 시도.
        var resp = await _factory.CreateClient().PostAsync("/api/u-vision/scenarios", Body("NoPin"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Mutation_WrongPin_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, "9999");
        var resp = await client.PostAsync("/api/u-vision/scenarios", Body("WrongPin"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task List_IsUnauthenticated()
    {
        // 운영 화면이 활성 시나리오 선택을 위해 목록을 읽을 수 있어야 한다(무인증).
        var resp = await _factory.CreateClient().GetAsync("/api/u-vision/scenarios");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Mutation_WhenPinUnset_Returns503()
    {
        // 별도 팩토리: ADMIN_PIN 미설정 → 관리 기능 비활성(503).
        using var unsetFactory = new UVisionApiFactory();
        var resp = await unsetFactory.CreateClient().PostAsync("/api/u-vision/scenarios", Body("x"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Create_LeverFields_RoundTrip()
    {
        // 다운스케일·refs 장수 레버(범용 도구) — wire(snake_case) 왕복 확인.
        var resp = await Admin().PostAsync("/api/u-vision/scenarios",
            JsonContent.Create(new { name = "Lever Test", max_image_dimension = 256, reference_cap = 2 }));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(256, created.GetProperty("max_image_dimension").GetInt32());
        Assert.Equal(2, created.GetProperty("reference_cap").GetInt32());

        var id = created.GetProperty("scenario_id").GetString();
        var got = await (await _factory.CreateClient().GetAsync($"/api/u-vision/scenarios/{id}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(256, got.GetProperty("max_image_dimension").GetInt32());
        Assert.Equal(2, got.GetProperty("reference_cap").GetInt32());
    }

    [Fact]
    public async Task Create_OmittingLeverFields_UsesSafeDefaults()
    {
        // 레버 미지정 → 보수적 기본(축소 없음 0, refs cap 4 = 종전 하드코딩) → 현 동작 보존.
        var resp = await Admin().PostAsync("/api/u-vision/scenarios", Body("Default Levers"));
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, created.GetProperty("max_image_dimension").GetInt32());
        Assert.Equal(4, created.GetProperty("reference_cap").GetInt32());
    }

    [Fact]
    public void Deserialize_LegacyScenarioJson_DefaultsLeverFields()
    {
        // 신규 필드가 없는 기존 scenario.json(하위호환) → record 기본값으로 역직렬화.
        const string legacy = """
            { "scenario_id": "legacy", "name": "Legacy", "criteria": "x" }
            """;

        var scenario = JsonSerializer.Deserialize<UVision.Api.Models.Scenario>(
            legacy, UVision.Api.Storage.StoragePaths.Json);

        Assert.NotNull(scenario);
        Assert.Equal(0, scenario!.MaxImageDimension);   // 축소 없음
        Assert.Equal(4, scenario.ReferenceCap);         // 종전 동작
    }

    [Fact]
    public void AllowCloudEgress_DefaultsFalse_AndRoundtrips()
    {
        var input = new ScenarioInput { Name = "n", AllowCloudEgress = true };
        var s = input.ToScenario("sc");
        Assert.True(s.AllowCloudEgress);
        Assert.False(new ScenarioInput { Name = "n" }.ToScenario("sc2").AllowCloudEgress);
    }
}
