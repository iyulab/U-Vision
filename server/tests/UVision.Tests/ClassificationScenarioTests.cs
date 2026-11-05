using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 선별(<c>classification</c>, 원장 D-111) — 시나리오 어휘 · 경계 검증 · 유효 시나리오 파생 · v1 거절.
/// 클래스에 선악이 없고(OK 가 없어도 된다) <c>verdict</c> 는 «꼭 잡을 클래스인가» 의 보기다.
/// </summary>
public class ClassificationScenarioTests : IClassFixture<PinFactory>
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private readonly PinFactory _factory;

    public ClassificationScenarioTests(PinFactory factory) => _factory = factory;

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        return client;
    }

    private static Scenario Classification(params ScenarioClass[] classes) =>
        new() { ScenarioId = "cls", Name = "cls", Type = ScenarioVocabulary.Classification, Classes = [.. classes] };

    private static readonly ScenarioClass[] Parts =
    [
        new() { Id = "P-100", Label = "품번 100", Description = "각진 금속 브래킷", Settleable = true },
        new() { Id = "P-200", Label = "품번 200", Description = "둥근 와셔" },
        new() { Id = "REJECT", Label = "분류 불가", MustCatch = true },
    ];

    // ── 유효 시나리오 파생 ────────────────────────────────────────────────

    [Fact]
    public void Resolve_Classification_IsValueNeutral_AndHasTheInspectionTiers()
    {
        var e = EffectiveScenario.Resolve(Classification(Parts), mlEnabled: true, memoryEnabled: true);

        Assert.True(e.IsClassification);
        Assert.True(e.IsMultiClass);                       // 결과는 언제나 클래스 id 로 남는다
        Assert.Equal(["memory", "ml", "vlm"], e.Tiers);    // 설계 §4.2 — inspection 과 같은 세 단
        Assert.Equal(["P-100", "P-200", "REJECT"], e.Labels);
        Assert.Empty(e.DefectTypes);                       // 불량 유형이라는 개념이 없다
    }

    [Fact]
    public void Resolve_Classification_VerdictIsTheMustCatchView()
    {
        var e = EffectiveScenario.Resolve(Classification(Parts), mlEnabled: false);

        Assert.Equal(Verdict.OK, e.VerdictOf("P-100"));
        Assert.Equal(Verdict.OK, e.VerdictOf("p-200"));    // 대소문자 무시(라벨 비교와 같다)
        Assert.Equal(Verdict.NG, e.VerdictOf("REJECT"));   // 꼭 잡을 클래스만 NG 로 보인다
        Assert.False(e.NgLabelRule!("P-100"));
        Assert.True(e.NgLabelRule!("REJECT"));             // 메트릭의 NG recall 은 must_catch recall
    }

    [Fact]
    public void Resolve_Classification_WithoutMustCatch_IsAlwaysOk()
    {
        var e = EffectiveScenario.Resolve(
            Classification(new ScenarioClass { Id = "A" }, new ScenarioClass { Id = "B" }), mlEnabled: false);

        Assert.All(e.Labels, label => Assert.Equal(Verdict.OK, e.VerdictOf(label)));
    }

    [Fact]
    public void ClassOf_Classification_TakesTheClassTheVlmPicked_AndRefusesUndeclaredOnes()
    {
        var e = EffectiveScenario.Resolve(Classification(Parts), mlEnabled: false);

        Assert.Equal("P-200", e.ClassOf(new InspectionResult { Verdict = Verdict.OK, Confidence = 0.9, Class = "p-200" }));
        // 선언하지 않은 클래스 · 클래스를 못 댄 답은 답이 아니다 — 파이프라인이 fail-closed 로 처리한다.
        Assert.Null(e.ClassOf(new InspectionResult { Verdict = Verdict.OK, Confidence = 0.9, Class = "P-999" }));
        Assert.Null(e.ClassOf(new InspectionResult { Verdict = Verdict.NG, Confidence = 0.9 }));
    }

    [Fact]
    public void Resolve_Inspection_IsUnchanged()
    {
        // 기존 유형의 파생은 이 유형이 생겨도 그대로다(골든 시험이 byte 로 한 번 더 지킨다).
        var e = EffectiveScenario.Resolve(new Scenario { ScenarioId = "s", Name = "s" }, mlEnabled: false);

        Assert.False(e.IsClassification);
        Assert.Equal(Verdict.OK, e.VerdictOf("OK"));
        Assert.Equal(Verdict.NG, e.VerdictOf("NG"));
        Assert.False(e.IsMultiClass);
    }

    // ── 경계 검증 ─────────────────────────────────────────────────────────

    public static TheoryData<string, string> Invalid => new()
    {
        { """{"name":"x","type":"classification"}""", "classes(둘 이상)" },
        { """{"name":"x","type":"classification","classes":[{"id":"A"}]}""", "둘 이상" },
        { """{"name":"x","type":"classification","classes":[{"id":"A"},{"id":"a"}]}""", "겹친다" },
        { """{"name":"x","type":"classification","classes":[{"id":"A","settleable":true,"must_catch":true},{"id":"B"}]}""", "settleable" },
        { """{"name":"x","type":"classification","classes":[{"id":"A","description":" 앞 공백"},{"id":"B"}]}""", "description" },
        { """{"name":"x","type":"classification","classes":[{"id":"A","description":"줄\n바꿈"},{"id":"B"}]}""", "description" },
        { """{"name":"x","type":"classification","classes":[{"id":"A"},{"id":"B"}],"judges":[{"tier":"vlm"},{"tier":"ml"}]}""", "꼭대기" },
        { """{"name":"x","type":"classification","classes":[{"id":"A"},{"id":"B"}],"judges":[{"tier":"match"},{"tier":"vlm"}]}""", "tier" },
        { """{"name":"x","type":"classification","classes":[{"id":"A"},{"id":"B"}],"expected_count":3}""", "counting" },
        { """{"name":"x","classes":[{"id":"OK","settleable":true,"description":"설명"},{"id":"NG","must_catch":true}]}""", "classification 일 때만" },
        { """{"name":"x","type":"verification","classes":[{"id":"MATCH","settleable":true,"description":"설명"},{"id":"MISMATCH","must_catch":true}]}""", "classification 일 때만" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Create_RejectsContradictoryClassification(string body, string reasonFragment)
    {
        var resp = await Admin().PostAsync("/api/u-vision/scenarios",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(reasonFragment, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_AcceptsClassesWithoutOk_AndKeepsDescriptions()
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(new
        {
            name = "품번 구분",
            type = "classification",
            classes = new object[]
            {
                new { id = "P-100", label = "품번 100", description = "각진 금속 브래킷", settleable = true },
                new { id = "P-200", label = "품번 200", description = "둥근 와셔" },
                new { id = "REJECT", must_catch = true },
            },
            judges = new[] { new { tier = "ml" }, new { tier = "vlm" } },
            memory_max_distance = 0.12,
        }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString();

        var got = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}");
        Assert.Equal("classification", got.GetProperty("type").GetString());
        var classes = got.GetProperty("classes");
        Assert.Equal(3, classes.GetArrayLength());
        Assert.Equal("각진 금속 브래킷", classes[0].GetProperty("description").GetString());
        Assert.False(classes[2].TryGetProperty("description", out _)); // 쓰지 않은 설명은 나타나지 않는다(additive)
        Assert.Equal(0.12, got.GetProperty("memory_max_distance").GetDouble(), 3);
    }

    [Fact]
    public async Task Update_ToClassification_RequiresClassesInTheSameRequest()
    {
        var create = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(new
        {
            name = "to be switched",
            classes = new object[] { new { id = "OK", settleable = true }, new { id = "NG", must_catch = true } },
        }));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString();

        // 유형을 바꾸면 검사의 OK/NG 는 옮겨 가지 않는다(저장소가 비운다) — 새 클래스를 함께 줘야 한다.
        var bare = await Admin().PutAsync($"/api/u-vision/scenarios/{id}",
            JsonContent.Create(new { name = "to be switched", type = "classification" }));
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Contains("classes(둘 이상)", await bare.Content.ReadAsStringAsync());

        var ok = await Admin().PutAsync($"/api/u-vision/scenarios/{id}", JsonContent.Create(new
        {
            name = "to be switched",
            type = "classification",
            classes = new object[] { new { id = "A", settleable = true }, new { id = "B" } },
        }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var got = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/u-vision/scenarios/{id}");
        Assert.Equal(["A", "B"], got.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
    }

    // ── v1 은 부를 수 없다 ────────────────────────────────────────────────

    [Fact]
    public async Task V1Inspect_IsRefused_ForClassification()
    {
        var client = _factory.CreateClient(); // 호스트를 띄워 데이터 디렉토리를 만든다
        var dir = Path.Combine(_factory.DataPath, "cls");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "scenario.json"),
            JsonSerializer.Serialize(Classification(Parts), StoragePaths.Json));

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("cls"), "scenario_id");

        var resp = await client.PostAsync("/api/u-vision/inspect", form);

        // OK/NG 로 접히지 않는 결과를 v1 의 OK/NG 로 속여 내지 않는다 — v2 를 안내한다.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("POST /v2/inspect", await resp.Content.ReadAsStringAsync());
    }
}
