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
        foreach (var key in new[] { "\"type\"", "\"trigger\"", "\"classes\"", "\"judges\"", "\"trust\"", "\"patch_encoder\"", "\"expected_count\"", "\"count_tolerance\"", "\"min_confidence\"", "\"count_classes\"" })
            Assert.DoesNotContain(key, json);
    }

    public static TheoryData<string, string> Invalid => new()
    {
        { """{"name":"x","type":"streaming"}""", "type" },
        { """{"name":"x","type":"counting"}""", "expected_count" },
        { """{"name":"x","type":"counting","expected_count":-1}""", "expected_count" },
        { """{"name":"x","type":"counting","expected_count":4,"count_tolerance":-1}""", "count_tolerance" },
        { """{"name":"x","type":"counting","expected_count":4,"min_confidence":0}""", "min_confidence" },
        { """{"name":"x","type":"counting","expected_count":4,"min_confidence":1.5}""", "min_confidence" },
        { """{"name":"x","type":"counting","expected_count":4,"count_classes":["helmet","helmet"]}""", "겹친다" },
        { """{"name":"x","type":"counting","expected_count":4,"count_classes":[" helmet"]}""", "count_classes" },
        { """{"name":"x","type":"counting","expected_count":4,"judges":[{"tier":"vlm"}]}""", "judges" },
        { """{"name":"x","expected_count":4}""", "counting" },
        { """{"name":"x","type":"verification","count_classes":["a"]}""", "counting" },
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
    public void Validate_PatchEncoder_IsALetterWhitelistNotAVocabulary(string encoder, bool valid)
    {
        var reason = EffectiveScenario.Validate(new ScenarioInput { Name = "x", PatchEncoder = encoder });
        Assert.Equal(valid, reason is null);
        if (!valid) Assert.Contains("patch_encoder", reason);
    }

    [Fact]
    public void Validate_PatchEncoder_RejectsMoreThan64Characters() =>
        Assert.NotNull(EffectiveScenario.Validate(new ScenarioInput { Name = "x", PatchEncoder = new string('a', 65) }));

    [Fact]
    public void Resolve_CarriesTheChosenPatchEncoder_AndLeavesItEmptyOtherwise()
    {
        Assert.Null(EffectiveScenario.Resolve(V1(), mlEnabled: false).PatchEncoder);
        Assert.Equal("enc-a", EffectiveScenario.Resolve(V1() with { PatchEncoder = "enc-a" }, mlEnabled: false).PatchEncoder);
    }

    [Fact]
    public async Task PatchEncoder_IsKeptWhenOmitted_ReplacedWhenGiven_ClearedByEmptyString_AndAbsentFromV1Json()
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(new { name = "enc keep", patch_encoder = "enc-a" }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString();
        async Task<JsonElement> GetAsync() => await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}");

        Assert.Equal("enc-a", (await GetAsync()).GetProperty("patch_encoder").GetString());

        await Admin().PutAsync($"/api/u-vision/scenarios/{id}", JsonContent.Create(new { name = "enc keep", criteria = "생략" }));
        Assert.Equal("enc-a", (await GetAsync()).GetProperty("patch_encoder").GetString());           // 생략 = 보존

        await Admin().PutAsync($"/api/u-vision/scenarios/{id}", JsonContent.Create(new { name = "enc keep", patch_encoder = "enc-b" }));
        Assert.Equal("enc-b", (await GetAsync()).GetProperty("patch_encoder").GetString());           // 값 = 교체

        await Admin().PutAsync($"/api/u-vision/scenarios/{id}", JsonContent.Create(new { name = "enc keep", patch_encoder = "" }));
        Assert.False((await GetAsync()).TryGetProperty("patch_encoder", out _));                      // 빈 문자열 = 지움(v1 JSON 으로 돌아간다)
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

    // ── 계수(counting) ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_Counting_HasOneDetectorTier_AndOkNgDecisionClasses()
    {
        var counting = V1() with { Type = "counting", ExpectedCount = 4, CountTolerance = 1, MinConfidence = 0.6, CountClasses = ["helmet"] };

        // 켜진 판정원과 상관없이 검출기 한 단 — VLM 은 세지 못하고 ML · 기억은 개수와 무관하다.
        var e = EffectiveScenario.Resolve(counting, mlEnabled: true, memoryEnabled: true);

        Assert.True(e.IsCounting);
        Assert.Equal(["detector"], e.Tiers);
        Assert.Equal(["OK", "NG"], e.Classes.Select(c => c.Id));
        Assert.Equal((4, 1, 0.6), (e.ExpectedCount, e.CountTolerance, e.MinConfidence));
        Assert.Equal(["helmet"], e.CountClasses);
    }

    [Fact]
    public void Resolve_NonCounting_IgnoresStrayCountFields()
    {
        var e = EffectiveScenario.Resolve(V1() with { ExpectedCount = 3 }, mlEnabled: false);

        Assert.False(e.IsCounting);
        Assert.Null(e.ExpectedCount);
    }

    private async Task<string> CreateCountingAsync(object body)
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(body));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString()!;
    }

    [Fact]
    public async Task Counting_RoundTrips_AndUpdateWithoutCountFields_KeepsThem()
    {
        var id = await CreateCountingAsync(new
        {
            name = "count keep", type = "counting", expected_count = 6, count_tolerance = 1, min_confidence = 0.7,
            count_classes = new[] { "helmet" },
        });

        // 계수를 모르는 수정(유형도 생략) — 기대 개수 등이 남는다.
        var put = await Admin().PutAsync($"/api/u-vision/scenarios/{id}",
            JsonContent.Create(new { name = "count keep", criteria = "바뀐 기준" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var got = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}");
        Assert.Equal("counting", got.GetProperty("type").GetString());
        Assert.Equal(6, got.GetProperty("expected_count").GetInt32());
        Assert.Equal(1, got.GetProperty("count_tolerance").GetInt32());
        Assert.Equal(0.7, got.GetProperty("min_confidence").GetDouble());
        Assert.Equal("helmet", got.GetProperty("count_classes")[0].GetString());
    }

    [Fact]
    public async Task Counting_UpdateWithTypeButWithoutExpected_ValidatesTheStoredExpected()
    {
        var id = await CreateCountingAsync(new { name = "count merge", type = "counting", expected_count = 3 });

        // 유형은 보내고 기대 개수는 생략 — 보존되므로 필수 검사를 통과한다.
        var put = await Admin().PutAsync($"/api/u-vision/scenarios/{id}",
            JsonContent.Create(new { name = "count merge", type = "counting", min_confidence = 0.9 }));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var got = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}");
        Assert.Equal(3, got.GetProperty("expected_count").GetInt32());
        Assert.Equal(0.9, got.GetProperty("min_confidence").GetDouble());
    }

    [Fact]
    public async Task Counting_ChangingTypeAway_DropsCountFields()
    {
        var id = await CreateCountingAsync(new { name = "count away", type = "counting", expected_count = 3, count_classes = new[] { "a" } });

        var put = await Admin().PutAsync($"/api/u-vision/scenarios/{id}",
            JsonContent.Create(new { name = "count away", type = "inspection" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var got = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}");
        Assert.False(got.TryGetProperty("expected_count", out _));
        Assert.False(got.TryGetProperty("count_classes", out _));
    }

    [Fact]
    public async Task Counting_ChangingTypeToCounting_RequiresAnExpectedCount_NotTheOldTypesLeftovers()
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(new { name = "to count" }));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString();

        var put = await Admin().PutAsync($"/api/u-vision/scenarios/{id}",
            JsonContent.Create(new { name = "to count", type = "counting" }));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("expected_count", await put.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OmittedTypeOnAVerificationScenario_ValidatesClassesAsVerification()
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(new { name = "ver keep", type = "verification" }));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString();

        // 유형을 생략한 수정은 저장된 유형(오투입 확인)의 클래스 규칙으로 본다 — 검사 규칙(OK 필수)으로 거부하지 않는다.
        var put = await Admin().PutAsync($"/api/u-vision/scenarios/{id}", JsonContent.Create(new
        {
            name = "ver keep",
            classes = new object[] { new { id = "MATCH", settleable = true }, new { id = "MISMATCH", must_catch = true } },
        }));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }
}
