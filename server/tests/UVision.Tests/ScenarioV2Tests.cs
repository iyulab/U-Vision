using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 시나리오 v2 읽기 — v1 만 적힌 시나리오가 지금 동작과 같은 유효 시나리오로 파생되는지, v2 필드가 비면 디스크 ·
/// 응답에 나타나지 않는지(byte 보존), 경계가 지어지지 않은 값을 거부하는지, 수정이 v2 구성을 지우지 않는지.
/// </summary>
public class ScenarioV2Tests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;

    public ScenarioV2Tests(PinFactory factory) => _factory = factory;

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        return client;
    }

    private static Scenario V1(string id = "s") => new() { ScenarioId = id, Name = id };

    [Fact]
    public void Resolve_V1Only_DerivesTodaysInspection()
    {
        var e = EffectiveScenario.Resolve(V1(), mlEnabled: false);

        Assert.Equal("inspection", e.Type);
        Assert.Equal("still", e.Trigger);
        Assert.Equal(["vlm"], e.Tiers);
        Assert.Equal("scenario-v1", e.SliceKey);
        Assert.Equal(ScenarioTargets.Default, e.Targets);
        Assert.Collection(e.Classes,
            ok => Assert.True(ok is { Id: "OK", Settleable: true, MustCatch: false }),
            ng => Assert.True(ng is { Id: "NG", Settleable: false, MustCatch: true }));
    }

    [Fact]
    public void Resolve_MlEnabled_PutsMlBelowVlm()
    {
        Assert.Equal(["ml", "vlm"], EffectiveScenario.Resolve(V1(), mlEnabled: true).Tiers);
    }

    [Theory]
    [InlineData(false, true, new[] { "memory", "vlm" })]
    [InlineData(true, true, new[] { "memory", "ml", "vlm" })]
    [InlineData(true, false, new[] { "ml", "vlm" })]
    public void Resolve_PutsEnabledJudges_InCostOrder(bool ml, bool memory, string[] expected)
    {
        Assert.Equal(expected, EffectiveScenario.Resolve(V1(), mlEnabled: ml, memoryEnabled: memory).Tiers);
    }

    [Fact]
    public void Resolve_Verification_NeverDerivesMemory()
    {
        var verification = V1() with { Type = "verification" };
        Assert.Equal(["match", "vlm"], EffectiveScenario.Resolve(verification, mlEnabled: true, memoryEnabled: true).Tiers);
    }

    [Fact]
    public void Resolve_ExplicitJudges_WinOverDerivation()
    {
        var scenario = V1() with { Judges = [new() { Tier = "vlm" }] };
        Assert.Equal(["vlm"], EffectiveScenario.Resolve(scenario, mlEnabled: true).Tiers);
    }

    [Fact]
    public void V1Scenario_OnDisk_HasNoV2Keys()
    {
        var json = JsonSerializer.Serialize(V1(), StoragePaths.Json);
        foreach (var key in new[] { "\"type\"", "\"trigger\"", "\"classes\"", "\"judges\"", "\"trust\"", "\"memory_encoder\"" })
            Assert.DoesNotContain(key, json);
    }

    public static TheoryData<string, string> Invalid => new()
    {
        { """{"name":"x","type":"counting"}""", "type" },
        { """{"name":"x","trigger":"stream"}""", "trigger" },
        { """{"name":"x","judges":[{"tier":"vlm"},{"tier":"ml"}]}""", "꼭대기" },
        { """{"name":"x","judges":[{"tier":"system1"},{"tier":"vlm"}]}""", "tier" },
        { """{"name":"x","judges":[]}""", "judges" },
        { """{"name":"x","classes":[{"id":"OK","settleable":true},{"id":"NG"}]}""", "must_catch" },
        { """{"name":"x","classes":[{"id":"OK","settleable":true,"must_catch":true},{"id":"NG","must_catch":true}]}""", "settleable" },
        { """{"name":"x","classes":[{"id":"OK"},{"id":"ok"}]}""", "겹친다" },
        { """{"name":"x","trust":{"slice_key":"scenario-part"}}""", "slice_key" },
        { """{"name":"x","trust":{"targets":{"agreement":1.5,"max_miss_rate":0.02,"sample_size":150}}}""", "targets" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Create_RejectsUnbuiltOrContradictoryV2(string body, string reasonFragment)
    {
        var resp = await Admin().PostAsync("/api/u-vision/scenarios",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(reasonFragment, await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("dinov2-small-mid6", true)]
    [InlineData("dinov2-small_cat9.v2", true)]
    [InlineData("", true)]                                  // 빈 문자열 = 지움
    [InlineData("../evil", false)]
    [InlineData("-leading-dash", false)]
    [InlineData("has space", false)]
    [InlineData("한글", false)]
    public void Validate_MemoryEncoder_IsALetterWhitelistNotAVocabulary(string encoder, bool valid)
    {
        var reason = EffectiveScenario.Validate(new ScenarioInput { Name = "x", MemoryEncoder = encoder });
        Assert.Equal(valid, reason is null);
        if (!valid) Assert.Contains("memory_encoder", reason);
    }

    [Fact]
    public void Validate_MemoryEncoder_RejectsMoreThan64Characters() =>
        Assert.NotNull(EffectiveScenario.Validate(new ScenarioInput { Name = "x", MemoryEncoder = new string('a', 65) }));

    [Fact]
    public void Resolve_CarriesTheChosenMemoryEncoder_AndLeavesItEmptyOtherwise()
    {
        Assert.Null(EffectiveScenario.Resolve(V1(), mlEnabled: false).MemoryEncoder);
        Assert.Equal("enc-a", EffectiveScenario.Resolve(V1() with { MemoryEncoder = "enc-a" }, mlEnabled: false).MemoryEncoder);
    }

    [Fact]
    public async Task MemoryEncoder_IsKeptWhenOmitted_ReplacedWhenGiven_ClearedByEmptyString_AndAbsentFromV1Json()
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(new { name = "enc keep", memory_encoder = "enc-a" }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString();
        async Task<JsonElement> GetAsync() => await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}");

        Assert.Equal("enc-a", (await GetAsync()).GetProperty("memory_encoder").GetString());

        await Admin().PutAsync($"/api/u-vision/scenarios/{id}", JsonContent.Create(new { name = "enc keep", criteria = "생략" }));
        Assert.Equal("enc-a", (await GetAsync()).GetProperty("memory_encoder").GetString());           // 생략 = 보존

        await Admin().PutAsync($"/api/u-vision/scenarios/{id}", JsonContent.Create(new { name = "enc keep", memory_encoder = "enc-b" }));
        Assert.Equal("enc-b", (await GetAsync()).GetProperty("memory_encoder").GetString());           // 값 = 교체

        await Admin().PutAsync($"/api/u-vision/scenarios/{id}", JsonContent.Create(new { name = "enc keep", memory_encoder = "" }));
        Assert.False((await GetAsync()).TryGetProperty("memory_encoder", out _));                      // 빈 문자열 = 지움(v1 JSON 으로 돌아간다)
    }

    [Fact]
    public async Task Update_WithoutV2Fields_KeepsStoredV2Configuration()
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(new
        {
            name = "v2 keep",
            judges = new[] { new { tier = "vlm" } },
            trust = new { targets = new { agreement = 0.9, max_miss_rate = 0.05, sample_size = 80 } },
        }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString();

        // v2 를 모르는 관리 화면의 수정 — v1 필드만 보낸다.
        var put = await Admin().PutAsync($"/api/u-vision/scenarios/{id}",
            JsonContent.Create(new { name = "v2 keep", criteria = "바뀐 기준" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var got = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}");
        Assert.Equal("바뀐 기준", got.GetProperty("criteria").GetString());
        Assert.Equal("vlm", got.GetProperty("judges")[0].GetProperty("tier").GetString());
        Assert.Equal(80, got.GetProperty("trust").GetProperty("targets").GetProperty("sample_size").GetInt32());
    }

    [Fact]
    public async Task Update_ChangingType_DropsTypeBoundClassesAndJudges()
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(new
        {
            name = "type switch",
            judges = new[] { new { tier = "vlm" } },
            classes = new object[] { new { id = "OK", settleable = true }, new { id = "NG", must_catch = true } },
        }));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString();

        var put = await Admin().PutAsync($"/api/u-vision/scenarios/{id}",
            JsonContent.Create(new { name = "type switch", type = "verification" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var got = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}");
        Assert.Equal("verification", got.GetProperty("type").GetString());
        // 검사의 OK/NG · 단이 오투입 확인으로 넘어가지 않는다 — 비어서 verification 기본값으로 파생된다.
        Assert.False(got.TryGetProperty("classes", out _));
        Assert.False(got.TryGetProperty("judges", out _));
    }

    [Fact]
    public async Task V1Scenario_Response_HasNoV2Keys()
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios",
            JsonContent.Create(new { name = "plain v1", criteria = "기준" }));
        var json = await create.Content.ReadAsStringAsync();

        Assert.DoesNotContain("\"judges\"", json);
        Assert.DoesNotContain("\"type\"", json);
    }
}
