using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Ml;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 선별(<c>classification</c>) 판정 — VLM 이 OK/NG 가 아니라 클래스를 직접 고르고, 결과는 v2 <c>class</c> · 레코드 <c>class</c> 이며
/// OK/NG 는 그 보기(꼭 잡을 클래스면 NG)다. 선언하지 않은 클래스는 답이 아니다(판정 불가, fail-closed).
/// 프롬프트 · 출력 스키마 · 답 읽기 · mock 판정원은 같은 규칙을 쓰는지.
/// </summary>
public class ClassificationInspectTests
{
    private const string Id = "parts";

    private static readonly ScenarioClass[] Parts =
    [
        new() { Id = "P-100", Label = "품번 100", Description = "각진 금속 브래킷", Settleable = true },
        new() { Id = "P-200", Label = "품번 200", Description = "둥근 와셔" },
        new() { Id = "REJECT", Label = "분류 불가", MustCatch = true },
    ];

    private static async Task SeedAsync(UVisionApiFactory factory, IReadOnlyList<ScenarioClass>? classes = null)
    {
        factory.CreateClient();
        var dir = Path.Combine(factory.DataPath, Id);
        Directory.CreateDirectory(dir);
        var scenario = new Scenario
        {
            ScenarioId = Id, Name = "품번 구분", Type = ScenarioVocabulary.Classification, Classes = [.. classes ?? Parts],
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "scenario.json"), JsonSerializer.Serialize(scenario, StoragePaths.Json));
    }

    private static MultipartFormDataContent Form()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(Id), "scenario_id");
        return form;
    }

    private static HttpClient Client(UVisionApiFactory factory, IVlmProvider vlm) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(vlm))).CreateClient();

    private static JsonElement StoredRecord(UVisionApiFactory factory)
    {
        var dayDir = Path.Combine(factory.DataPath, Id, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(dayDir, "img_*.json").Single())).RootElement;
    }

    private static async Task<JsonElement> V2Async(HttpClient client)
    {
        var resp = await client.PostAsync("/api/u-vision/v2/inspect", Form());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    // ── 판정 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VlmPicksAClass_V2CarriesIt_RecordKeepsIt_AndVerdictIsTheMustCatchView()
    {
        using var factory = new UVisionApiFactory();
        await SeedAsync(factory);
        var vlm = new FixedVlm("p-200"); // 철자는 선언대로 맞춘다
        var body = await V2Async(Client(factory, vlm));

        Assert.Equal("P-200", body.GetProperty("class").GetString());
        var record = StoredRecord(factory);
        Assert.Equal("P-200", record.GetProperty("class").GetString());
        Assert.Equal("OK", record.GetProperty("verdict").GetString()); // 꼭 잡을 클래스가 아니다
        // VLM 은 OK/NG 가 아니라 클래스 목록(설명 포함)을 받는다.
        Assert.Equal(["P-100", "P-200", "REJECT"], vlm.SeenClasses);
        Assert.Equal("둥근 와셔", vlm.SeenDescriptions!["P-200"]);
    }

    [Fact]
    public async Task VlmPicksAMustCatchClass_VerdictIsNg()
    {
        using var factory = new UVisionApiFactory();
        await SeedAsync(factory);
        var body = await V2Async(Client(factory, new FixedVlm("REJECT")));

        Assert.Equal("REJECT", body.GetProperty("class").GetString());
        Assert.Equal("NG", StoredRecord(factory).GetProperty("verdict").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("P-999")]
    [InlineData("OK")]   // 선별에는 OK 라는 클래스가 없다 — 모델이 습관대로 OK/NG 로 답해도 답이 아니다
    public async Task VlmWithoutADeclaredClass_IsUnavailable_AndNothingIsStored(string? picked)
    {
        using var factory = new UVisionApiFactory();
        await SeedAsync(factory);
        var resp = await Client(factory, new FixedVlm(picked)).PostAsync("/api/u-vision/v2/inspect", Form());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var dayDir = Path.Combine(factory.DataPath, Id, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.False(Directory.Exists(dayDir) && Directory.GetFiles(dayDir, "img_*.json").Length > 0);
    }

    [Fact]
    public async Task ClassesWithoutOk_FlowThroughTheLedgerAndMetrics_WithoutError()
    {
        // 어느 클래스도 settleable 이 아니고 OK 도 없다 — 라우터 정책이 빈 확정 집합으로 서는지(원장의 «아무것도 확정하지 않음» 기본).
        using var factory = UVisionApiFactory.Create(mlClassifier: new FixedClassifier("A"));
        await SeedAsync(factory, [new ScenarioClass { Id = "A" }, new ScenarioClass { Id = "B" }]);
        var body = await V2Async(Client(factory, new FixedVlm("B")));

        Assert.Equal("B", body.GetProperty("class").GetString());
        var path = body.GetProperty("path").EnumerateArray().Select(s => (s.GetProperty("tier").GetString(), s.GetProperty("outcome").GetString())).ToList();
        Assert.Contains(("ml", "escalated"), path);          // 아무것도 확정하지 않으므로 아래 단의 답은 위로 올라간다
        Assert.Contains(("vlm", "settled"), path);
    }

    [Fact]
    public async Task SettleableClass_IsTheOnlyOneTheLowerTierMayAnswerFor()
    {
        using var factory = UVisionApiFactory.Create(mlClassifier: new FixedClassifier("P-100"));
        await SeedAsync(factory);
        var settleable = await V2Async(Client(factory, new FixedVlm("P-100")));
        Assert.Contains(("ml", "shadowed"),
            settleable.GetProperty("path").EnumerateArray().Select(s => (s.GetProperty("tier").GetString(), s.GetProperty("outcome").GetString())));

        using var factory2 = UVisionApiFactory.Create(mlClassifier: new FixedClassifier("P-200"));
        await SeedAsync(factory2);
        var plain = await V2Async(Client(factory2, new FixedVlm("P-200")));
        Assert.Contains(("ml", "escalated"),
            plain.GetProperty("path").EnumerateArray().Select(s => (s.GetProperty("tier").GetString(), s.GetProperty("outcome").GetString())));
    }

    [Fact]
    public async Task MlAndVlmDisagreeOnTheClass_RequiresReview()
    {
        using var factory = UVisionApiFactory.Create(mlClassifier: new FixedClassifier("P-200"));
        await SeedAsync(factory);
        var body = await V2Async(Client(factory, new FixedVlm("P-100")));

        Assert.Equal("P-100", body.GetProperty("class").GetString()); // 판정은 아직 VLM(권한 단계 advisory)
        var record = StoredRecord(factory);
        Assert.False(record.GetProperty("agreement").GetBoolean());
        Assert.True(record.GetProperty("requires_review").GetBoolean());
    }

    [Fact]
    public async Task MlLabelOutsideTheScenario_IsNotUsed()
    {
        using var factory = UVisionApiFactory.Create(mlClassifier: new FixedClassifier("rust"));
        await SeedAsync(factory);
        var body = await V2Async(Client(factory, new FixedVlm("P-100")));

        Assert.Equal("P-100", body.GetProperty("class").GetString());
        Assert.False(StoredRecord(factory).TryGetProperty("ml", out _)); // 2중체크 없이 VLM 단독(degrade)
    }

    [Fact]
    public async Task Inspect_WritesTheClassIntoTheMetricsRow_AndTheEndpointShowsTheBlock()
    {
        using var factory = new UVisionApiFactory();
        await SeedAsync(factory);
        var client = Client(factory, new FixedVlm("P-200"));
        var body = await V2Async(client);
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = Id, Date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ImageId = body.GetProperty("decision_id").GetString()!, Label = "P-100" });

        var metrics = await client.GetFromJsonAsync<JsonElement>($"/api/u-vision/metrics?scenario_id={Id}");

        var c = metrics.GetProperty("classification");
        Assert.Equal(1, c.GetProperty("checked").GetInt32());
        Assert.Equal(1, c.GetProperty("predicted").GetProperty("P-200").GetInt32());
        Assert.Equal(1, c.GetProperty("confusion").GetProperty("P-100").GetProperty("P-200").GetInt32()); // 사람은 P-100, 판정은 P-200
        Assert.Equal(0.0, c.GetProperty("agreement_rate").GetDouble());
    }

    // ── 라벨 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("P-100", HttpStatusCode.OK)]
    [InlineData("REJECT", HttpStatusCode.OK)]
    [InlineData("OK", HttpStatusCode.BadRequest)]   // 사람 라벨의 어휘도 시나리오의 클래스 id 다
    [InlineData("p-100", HttpStatusCode.BadRequest)] // 철자 그대로 — 학습 폴더 이름이 된다
    public async Task HumanLabel_VocabularyIsTheScenarioClasses(string label, HttpStatusCode expected)
    {
        using var factory = new UVisionApiFactory();
        await SeedAsync(factory);
        await factory.SeedResultAsync(Id, "2026-10-22", "img_0123456789abcdef0123456789abcdef");
        var resp = await factory.CreateClient().PutAsync("/api/u-vision/results/label", JsonContent.Create(new
        {
            scenario_id = Id, date = "2026-10-22", image_id = "img_0123456789abcdef0123456789abcdef", label,
        }));

        Assert.Equal(expected, resp.StatusCode);
    }

    // ── 프롬프트 · 출력 스키마 · 답 읽기 · mock ────────────────────────────

    private static ScenarioContext Context(IReadOnlyList<ScenarioClass>? classes = null) =>
        new() { ScenarioId = Id, Name = "품번 구분", Criteria = "금속 부품의 품번을 가린다", Classes = classes ?? Parts };

    [Fact]
    public void Prompt_ListsTheClassesWithDescriptions_AndDoesNotAskForOkNg()
    {
        var prompt = VlmPrompt.BuildSystemPrompt(Context());

        Assert.Contains("- P-100(품번 100): 각진 금속 브래킷", prompt);
        Assert.Contains("- P-200(품번 200): 둥근 와셔", prompt);
        Assert.Contains("- REJECT(분류 불가)", prompt);            // 설명이 없는 클래스는 이름만
        Assert.Contains("금속 부품의 품번을 가린다", prompt);
        Assert.DoesNotContain("OK 또는 NG 로 판정", prompt);
        Assert.DoesNotContain("defect_class", prompt);
        Assert.Contains("class 는 위 id 중 하나", prompt);
    }

    [Fact]
    public void Prompt_ForInspection_IsUnchanged()
    {
        var prompt = VlmPrompt.BuildSystemPrompt(new ScenarioContext { ScenarioId = "s", Criteria = "기준" });

        Assert.Contains("OK 또는 NG 로 판정한다", prompt);
        Assert.DoesNotContain("[클래스]", prompt);
    }

    [Fact]
    public void ReferenceTag_OfAClassificationReference_IsTheClass_NotOkNg()
    {
        var reference = new ReferenceImage { Data = new byte[] { 1 }, Label = ReferenceLabel.Ok, ClassId = "P-200" };

        Assert.Equal("[P-200(품번 200) 기준]", VlmPrompt.ReferenceTag(reference, null, Parts));
        Assert.Equal("[알수없음 기준]", VlmPrompt.ReferenceTag(reference with { ClassId = "알수없음" }, null, Parts));
        Assert.Equal("[OK 기준]", VlmPrompt.ReferenceTag(reference with { ClassId = null }, null, null)); // 검사의 표지는 그대로
    }

    [Fact]
    public void ClassSchema_ConstrainsClassToDeclaredIds_AndHasNoVerdict()
    {
        var schema = IronHiveVlmProvider.ClassSchema(["P-100", "P-200"]).ToJsonString();

        Assert.Contains("\"class\":{\"type\":\"string\",\"enum\":[\"P-100\",\"P-200\"]}", schema);
        Assert.DoesNotContain("verdict", schema);
        Assert.Contains("\"required\":[\"class\",\"findings\",\"confidence\"]", schema);
    }

    [Fact]
    public void ParseClassAnswer_DerivesTheVerdictViewFromTheClass()
    {
        var picked = IronHiveVlmProvider.ParseClassAnswer(
            """{"class":"REJECT","findings":"표식 없음","confidence":0.8}""", Parts)!;

        Assert.Equal("REJECT", picked.Class);
        Assert.Equal(Verdict.NG, picked.Verdict);
        Assert.Equal("표식 없음", picked.Findings);

        var other = IronHiveVlmProvider.ParseClassAnswer("""{"class":"P-100","findings":"","confidence":0.9}""", Parts)!;
        Assert.Equal(Verdict.OK, other.Verdict);
    }

    [Fact]
    public async Task MockVlm_PicksADeclaredClass_Deterministically()
    {
        var mock = new MockVlmProvider();
        var a = await mock.InspectAsync(new byte[] { 1, 2, 3, 4 }, Context());
        var b = await mock.InspectAsync(new byte[] { 1, 2, 3, 4 }, Context());

        Assert.Equal(a.Class, b.Class);
        Assert.Contains(a.Class, Parts.Select(c => c.Id));
        Assert.Equal(ScenarioClass.ViewOf(Parts, a.Class!), a.Verdict);
    }

    // ── 시험 대역 ─────────────────────────────────────────────────────────

    private sealed class FixedVlm(string? picked) : IVlmProvider
    {
        public IReadOnlyList<string>? SeenClasses { get; private set; }
        public IReadOnlyDictionary<string, string?>? SeenDescriptions { get; private set; }
        public string Name => "fixed";

        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken cancellationToken = default)
        {
            SeenClasses = scenario.Classes?.Select(c => c.Id).ToList();
            SeenDescriptions = scenario.Classes?.ToDictionary(c => c.Id, c => c.Description);
            // 선별의 VLM 은 verdict 를 내지 않는다 — 자리표시자 OK 는 파이프라인이 클래스에서 다시 파생한다.
            return Task.FromResult(new InspectionResult { Verdict = Verdict.OK, Findings = "", Confidence = 0.9, Class = picked });
        }
    }

    private sealed class FixedClassifier(string label) : IMlClassifier
    {
        public string Name => "fixed";
        public bool IsEnabled => true;

        public Task<MlClassification> ClassifyAsync(
            ReadOnlyMemory<byte> image, string scenarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MlClassification
            {
                Label = label, Confidence = 0.9, Scores = new Dictionary<string, double> { [label] = 0.9 },
            });
    }
}
