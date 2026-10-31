using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HoneAI;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Counting;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 계수 시나리오의 판정 — 검출기가 낸 상자를 세어 기대와 견주고(v2 <c>count</c> · 레코드 <c>count</c> · v1 은 OK/NG 보기),
/// 검출기가 없거나 답하지 못하면 개수를 모르는 채 OK 를 내지 않고(503, 레코드 없음), 문턱 근처 상자가 결론을 바꾸면 NG + 사람 확인이다.
/// VLM 은 부르지 않는다. 검출기는 대본대로 답하는 대역이다 — 실 <c>mloop serve</c> 왕복이 아니다.
/// </summary>
public class CountingInspectTests
{
    private const string ScenarioId = "pills";

    private sealed class ScriptedDetector(Func<DetectionSet> script) : IObjectDetector
    {
        public int Calls { get; private set; }
        public string Name => "scripted";
        public bool IsEnabled => true;

        public Task<DetectionSet> DetectAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(script());
        }
    }

    private sealed class ThrowingDetector : IObjectDetector
    {
        public string Name => "throwing";
        public bool IsEnabled => true;

        public Task<DetectionSet> DetectAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated detector down");
    }

    /// <summary>VLM 이 불리면 실패하는 대역 — 계수는 VLM 을 부르지 않는다.</summary>
    private sealed class ForbiddenVlm : IVlmProvider
    {
        public string Name => "forbidden";
        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken ct = default) =>
            throw new InvalidOperationException("counting must not call the VLM");
    }

    private static Detection Box(double confidence, string cls = "pill") => new(cls, 0.1, 0.1, 0.2, 0.2, confidence);

    private static DetectionSet Boxes(params double[] confidences) => new([.. confidences.Select(c => Box(c))], "sha-abc");

    private static void Seed(UVisionApiFactory factory, int expected = 3, int? tolerance = null, double? minConfidence = null,
        string[]? kinds = null)
    {
        factory.CreateClient();
        factory.SeedScenario(new Scenario
        {
            ScenarioId = ScenarioId,
            Name = "알약 계수",
            Type = "counting",
            ExpectedCount = expected,
            CountTolerance = tolerance,
            MinConfidence = minConfidence,
            CountClasses = kinds is null ? null : [.. kinds],
        });
    }

    private static HttpClient Client(UVisionApiFactory factory, IObjectDetector detector) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton(detector);
            s.AddSingleton<IVlmProvider>(new ForbiddenVlm());
        })).CreateClient();

    private static MultipartFormDataContent Form()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(ScenarioId), "scenario_id");
        return form;
    }

    private static string Day => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonElement StoredRecord(UVisionApiFactory factory) =>
        JsonDocument.Parse(File.ReadAllText(
            Directory.GetFiles(Path.Combine(factory.DataPath, ScenarioId, Day), "img_*.json").Single())).RootElement;

    private static async Task<ProvenanceRecord> LedgerLineAsync(UVisionApiFactory factory)
    {
        var records = new List<ProvenanceRecord>();
        await foreach (var r in new JsonlProvenanceSink(Path.Combine(factory.DataPath, ScenarioId, "ledger", Day + ".jsonl")).ReadAsync())
            records.Add(r);
        return Assert.Single(records);
    }

    // ── 판정 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountMatchesExpected_IsOk_WithCountEvidence_AndWithoutCallingTheVlm()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 3);
        var detector = new ScriptedDetector(() => Boxes(0.95, 0.9, 0.8));

        var resp = await Client(factory, detector).PostAsync("/api/u-vision/v2/inspect", Form());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OK", body.GetProperty("class").GetString());
        Assert.Equal("detector", body.GetProperty("settled_by").GetProperty("tier").GetString());
        var count = body.GetProperty("count");
        Assert.Equal(3, count.GetProperty("total").GetInt32());
        Assert.Equal(3, count.GetProperty("by_class").GetProperty("pill").GetInt32());
        Assert.Equal(3, count.GetProperty("expected").GetInt32());
        Assert.Equal(0, count.GetProperty("tolerance").GetInt32());
        Assert.Equal(0.5, count.GetProperty("min_confidence").GetDouble());
        Assert.False(count.GetProperty("uncertain").GetBoolean());
        Assert.Equal(3, count.GetProperty("boxes").GetArrayLength());
        Assert.Equal("sha-abc", count.GetProperty("model_sha256").GetString());
        // 흔들림 · 검토가 없으면 그 신호가 응답에 없다.
        Assert.False(body.TryGetProperty("requires_review", out _));
        Assert.False(body.TryGetProperty("posture", out _));
        Assert.Equal(0.8, body.GetProperty("confidence").GetDouble(), precision: 6); // 센 상자 가운데 가장 약한 근거
        var step = Assert.Single(body.GetProperty("path").EnumerateArray());
        Assert.Equal("detector", step.GetProperty("tier").GetString());
        Assert.Equal("settled", step.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task CountOutsideTolerance_IsNg_AndV1ShowsItAsNgWithAFinding()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 3);
        var client = Client(factory, new ScriptedDetector(() => Boxes(0.95, 0.9)));

        var v1 = await client.PostAsync("/api/u-vision/inspect", Form());

        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        var body = await v1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NG", body.GetProperty("verdict").GetString());
        Assert.Contains("센 개수 2", body.GetProperty("findings").GetString());
        Assert.Contains("허용 범위 밖", body.GetProperty("findings").GetString());
        Assert.False(body.TryGetProperty("count", out _)); // v1 은 OK/NG 보기라 근거 모양을 싣지 않는다
    }

    [Fact]
    public async Task ToleranceAndCountedKinds_AreHonoured()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 4, tolerance: 1, kinds: ["pill"]);
        var detector = new ScriptedDetector(() => new DetectionSet(
            [Box(0.9), Box(0.9), Box(0.9), Box(0.9, "capsule"), Box(0.9, "capsule")])); // pill 3 + capsule 2

        var body = await (await Client(factory, detector).PostAsync("/api/u-vision/v2/inspect", Form()))
            .Content.ReadFromJsonAsync<JsonElement>();

        // pill 만 센다 → 3, 기대 4 ± 1 안.
        Assert.Equal("OK", body.GetProperty("class").GetString());
        Assert.Equal(3, body.GetProperty("count").GetProperty("total").GetInt32());
        Assert.False(body.GetProperty("count").GetProperty("by_class").TryGetProperty("capsule", out _));
    }

    [Fact]
    public async Task ScenarioMinConfidence_OverridesTheInstallDefault()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 1, minConfidence: 0.7);
        // 0.55 는 설치 기본(0.5)으로는 세지만 시나리오 문턱 0.7 로는 세지 않는다 — 두 상자 모두 문턱 ± 0.1 띠 밖이라 흔들림도 아니다.
        var client = Client(factory, new ScriptedDetector(() => Boxes(0.95, 0.55)));

        var body = await (await client.PostAsync("/api/u-vision/v2/inspect", Form())).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("count").GetProperty("total").GetInt32());
        Assert.Equal(0.7, body.GetProperty("count").GetProperty("min_confidence").GetDouble());
        Assert.Equal("OK", body.GetProperty("class").GetString());
    }

    [Fact]
    public async Task BoxNearTheThreshold_ThatChangesTheOutcome_IsNg_WithReviewBlock()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 3); // 문턱 0.5
        // 0.45 상자는 문턱 아래(안 센다)지만 ± 0.1 띠 안 — 넣으면 3, 빼면 2 라 결론이 흔들린다.
        var client = Client(factory, new ScriptedDetector(() => Boxes(0.95, 0.9, 0.55, 0.45)));

        var body = await (await client.PostAsync("/api/u-vision/v2/inspect", Form())).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("NG", body.GetProperty("class").GetString());
        Assert.True(body.GetProperty("count").GetProperty("uncertain").GetBoolean());
        Assert.True(body.GetProperty("requires_review").GetBoolean());
        Assert.Equal("review_block", body.GetProperty("posture").GetString());
    }

    // ── 기록 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Record_CarriesTheCount_AndTheLedgerNamesTheDetectorTierAndModel()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 2);
        var client = Client(factory, new ScriptedDetector(() => Boxes(0.9, 0.8)));

        await client.PostAsync("/api/u-vision/v2/inspect", Form());

        var record = StoredRecord(factory);
        Assert.Equal("OK", record.GetProperty("verdict").GetString());
        Assert.Equal(2, record.GetProperty("count").GetProperty("total").GetInt32());
        Assert.False(record.TryGetProperty("class", out _)); // 이분 판정 — verdict 가 곧 클래스
        var provenance = (await LedgerLineAsync(factory)).Provenance;
        Assert.Equal("detector", provenance.TierId);
        Assert.Equal("sha-abc", provenance.TierVersion); // 장부의 단 판 = 검출 모델 파일의 해시
        Assert.Equal("2", provenance.Annotations!["count:total"]);
        Assert.Equal("2", provenance.Annotations["count:expected"]);
        Assert.Equal("sha-abc", provenance.Annotations["detector:model_sha256"]);
        Assert.Equal("detector", provenance.Annotations["operative:source"]);
    }

    [Fact]
    public async Task NoMetricsRowIsWritten_ForCounting()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 1);

        await Client(factory, new ScriptedDetector(() => Boxes(0.9))).PostAsync("/api/u-vision/v2/inspect", Form());

        // 지금의 메트릭 행은 VLM · ML 의 신뢰도를 전제한 집계의 입력이다 — 검출기 점수를 섞지 않는다.
        Assert.False(Directory.Exists(Path.Combine(factory.DataPath, ScenarioId, "metrics")));
    }

    // ── 검출기가 없거나 답하지 못함 = 판정 불가 ───────────────────────────────

    [Fact]
    public async Task DetectorOff_Returns503_WithoutARecord_AndNeverOk()
    {
        using var factory = new UVisionApiFactory(); // Detection:Provider = none (기본)
        Seed(factory, expected: 0); // 기대 0 — 아무것도 못 센 사진이 «OK» 로 보이기 쉬운 경우
        var client = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IVlmProvider>(new ForbiddenVlm()))).CreateClient();

        var resp = await client.PostAsync("/api/u-vision/v2/inspect", Form());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("detection_unavailable").GetBoolean());
        Assert.Contains("검출기", body.GetProperty("reason").GetString());
        Assert.False(body.TryGetProperty("class", out _));
        Assert.False(Directory.Exists(Path.Combine(factory.DataPath, ScenarioId, Day))); // 레코드 없음
        Assert.Equal("abstained", body.GetProperty("path")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task DetectorThrows_Returns503_OnV1Too()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 3);

        var resp = await Client(factory, new ThrowingDetector()).PostAsync("/api/u-vision/inspect", Form());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("detection_unavailable").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(factory.DataPath, ScenarioId, Day)));
    }

    // ── 계수가 아닌 시나리오는 그대로 ───────────────────────────────────────

    [Fact]
    public async Task NonCountingScenario_NeverGetsACountField_AndNeverCallsTheDetector()
    {
        using var factory = new UVisionApiFactory();
        var detector = new ScriptedDetector(() => Boxes(0.9));
        var client = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IObjectDetector>(detector))).CreateClient();

        var resp = await client.PostAsync("/api/u-vision/v2/inspect",
            new MultipartFormDataContent
            {
                { new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } }, "image", "c.jpg" },
                { new StringContent("demo"), "scenario_id" },
            });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False((await resp.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("count", out _));
        Assert.Equal(0, detector.Calls);
    }
}
