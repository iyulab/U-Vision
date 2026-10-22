using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
/// 불량 유형을 선언한 검사 — 판정이 유형까지 내려가고(v2 <c>class</c> · 레코드 <c>class</c>), v1 은 그것을 NG 로 보이며,
/// 유형을 대지 못한 VLM 답은 판정 불가, 시나리오에 없는 ML 라벨은 판정에 쓰지 않는다.
/// </summary>
public class DefectClassInspectTests
{
    private const string ScenarioId = "typed";

    private static async Task SeedTypedAsync(UVisionApiFactory factory)
    {
        factory.CreateClient();
        var dir = Path.Combine(factory.DataPath, ScenarioId);
        Directory.CreateDirectory(dir);
        var scenario = new Scenario
        {
            ScenarioId = ScenarioId,
            Name = "유형 검사",
            Classes =
            [
                new() { Id = "OK", Settleable = true },
                new() { Id = "scratch", MustCatch = true },
                new() { Id = "dent", MustCatch = true },
            ],
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "scenario.json"), JsonSerializer.Serialize(scenario, StoragePaths.Json));
    }

    private static MultipartFormDataContent Form()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(ScenarioId), "scenario_id");
        return form;
    }

    private static HttpClient Client(UVisionApiFactory factory, IVlmProvider vlm) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(vlm))).CreateClient();

    private static JsonElement StoredRecord(UVisionApiFactory factory)
    {
        var dayDir = Path.Combine(factory.DataPath, ScenarioId,
            DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(dayDir, "img_*.json").Single())).RootElement;
    }

    [Fact]
    public async Task VlmNamesADefect_V2CarriesIt_V1SaysNg_RecordKeepsIt()
    {
        using var factory = new UVisionApiFactory();
        await SeedTypedAsync(factory);
        var vlm = new FixedVlm(Verdict.NG, "DENT"); // 철자는 선언대로 맞춘다
        var client = Client(factory, vlm);

        var v2 = await client.PostAsync("/api/u-vision/v2/inspect", Form());
        Assert.Equal(HttpStatusCode.OK, v2.StatusCode);
        var body = JsonDocument.Parse(await v2.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("dent", body.GetProperty("class").GetString());
        Assert.Equal("dent", StoredRecord(factory).GetProperty("class").GetString());
        Assert.Equal("NG", StoredRecord(factory).GetProperty("verdict").GetString());
        Assert.Equal(["scratch", "dent"], vlm.SeenDefectClasses);

        var v1 = await client.PostAsync("/api/u-vision/inspect", Form());
        var v1Body = JsonDocument.Parse(await v1.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("NG", v1Body.GetProperty("verdict").GetString());
        Assert.False(v1Body.TryGetProperty("class", out _));
    }

    [Fact]
    public async Task VlmOk_RecordHasClassOk()
    {
        using var factory = new UVisionApiFactory();
        await SeedTypedAsync(factory);
        var resp = await Client(factory, new FixedVlm(Verdict.OK, null)).PostAsync("/api/u-vision/v2/inspect", Form());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("OK", StoredRecord(factory).GetProperty("class").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("rust")]
    public async Task VlmNgWithoutADeclaredDefect_IsUnavailable(string? defect)
    {
        using var factory = new UVisionApiFactory();
        await SeedTypedAsync(factory);
        var resp = await Client(factory, new FixedVlm(Verdict.NG, defect)).PostAsync("/api/u-vision/inspect", Form());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task BinaryScenario_VlmIsNotAskedForDefectTypes_AndRecordHasNoClass()
    {
        using var factory = new UVisionApiFactory();
        var vlm = new FixedVlm(Verdict.NG, "scratch");
        var resp = await Client(factory, vlm).PostAsync("/api/u-vision/inspect", BinaryForm());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(vlm.SeenDefectClasses);
        var dayDir = Path.Combine(factory.DataPath, "demo", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.DoesNotContain("\"class\"", File.ReadAllText(Directory.GetFiles(dayDir, "img_*.json").Single()));
    }

    [Fact]
    public async Task MlLabelOutsideTheScenario_IsNotUsed()
    {
        using var factory = UVisionApiFactory.Create(mlClassifier: new FixedClassifier("rust"));
        await SeedTypedAsync(factory);
        var resp = await Client(factory, new FixedVlm(Verdict.NG, "scratch")).PostAsync("/api/u-vision/inspect", Form());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.TryGetProperty("ml", out _)); // 2중체크 없이 VLM 단독(degrade)
    }

    [Fact]
    public async Task MlAgreement_ComparesTheDefectType()
    {
        using var factory = UVisionApiFactory.Create(mlClassifier: new FixedClassifier("dent"));
        await SeedTypedAsync(factory);
        var resp = await Client(factory, new FixedVlm(Verdict.NG, "scratch")).PostAsync("/api/u-vision/inspect", Form());

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("agreement").GetBoolean()); // 둘 다 NG 지만 유형이 다르다
        Assert.True(body.GetProperty("requires_review").GetBoolean());
    }

    [Theory]
    [InlineData(ScenarioId, "scratch", HttpStatusCode.OK)]
    [InlineData(ScenarioId, "OK", HttpStatusCode.OK)]
    [InlineData(ScenarioId, "NG", HttpStatusCode.BadRequest)]   // 유형 시나리오는 유형을 댄다
    [InlineData(ScenarioId, "Scratch", HttpStatusCode.BadRequest)] // 철자 그대로 — 학습 폴더 이름이 된다
    [InlineData("demo", "NG", HttpStatusCode.OK)]
    [InlineData("demo", "scratch", HttpStatusCode.BadRequest)]
    [InlineData("gone", "NG", HttpStatusCode.OK)]                // 삭제된 시나리오의 지난 결과는 OK/NG
    public async Task Label_AllowedSetComesFromTheScenario(string scenarioId, string label, HttpStatusCode expected)
    {
        using var factory = new UVisionApiFactory();
        await SeedTypedAsync(factory);
        var resp = await factory.CreateClient().PutAsync("/api/u-vision/results/label", System.Net.Http.Json.JsonContent.Create(new
        {
            scenario_id = scenarioId, date = "2026-10-22", image_id = "img_0123456789abcdef0123456789abcdef", label,
        }));

        Assert.Equal(expected, resp.StatusCode);
    }

    private static MultipartFormDataContent BinaryForm()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        return form;
    }

    private sealed class FixedVlm(Verdict verdict, string? defect) : IVlmProvider
    {
        public IReadOnlyList<string>? SeenDefectClasses { get; private set; }
        public string Name => "fixed";

        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken cancellationToken = default)
        {
            SeenDefectClasses = scenario.DefectClasses;
            return Task.FromResult(new InspectionResult
            {
                Verdict = verdict, Findings = "", Confidence = 0.9, DefectClass = defect,
            });
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
