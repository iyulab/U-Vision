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
        Assert.False(count.TryGetProperty("near_boxes", out _)); // 근처 상자가 없으면 필드가 없다
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
        // 흔들림의 원인 — 세지 않은 근처 상자(0.45)가 응답에 실려 확인하는 사람이 사진에서 찾는다.
        var near = Assert.Single(body.GetProperty("count").GetProperty("near_boxes").EnumerateArray());
        Assert.Equal(0.45, near.GetProperty("confidence").GetDouble());
        Assert.Equal(3, body.GetProperty("count").GetProperty("boxes").GetArrayLength());
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

    /// <summary>값이 있는가 — 속성이 없거나 <c>null</c> 이면 없는 것이다(기존 행은 null 을 명시로 쓰는 필드가 있다).</summary>
    private static bool Has(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static List<JsonElement> MetricsRows(UVisionApiFactory factory)
    {
        var dir = Path.Combine(factory.DataPath, ScenarioId, "metrics");
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.GetFiles(dir, "*.jsonl").SelectMany(File.ReadAllLines)
            .Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement)];
    }

    [Fact]
    public async Task MetricsRow_IsWrittenWithTheCountMarker_AndNoDetectorScoresOrMlFields()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 1);

        await Client(factory, new ScriptedDetector(() => Boxes(0.9))).PostAsync("/api/u-vision/v2/inspect", Form());

        // 계수 행은 쓰되 표지(count)를 달아 집계가 VLM · ML 집계와 갈라 센다 — 검출기 점수 · ML 필드는 싣지 않는다.
        var row = Assert.Single(MetricsRows(factory));
        Assert.Equal("OK", row.GetProperty("verdict").GetString());
        Assert.True(row.TryGetProperty("count", out var count));
        Assert.False(count.TryGetProperty("uncertain", out _));   // 흔들리지 않으면 생략
        Assert.False(count.TryGetProperty("missing", out _));     // 총 개수 기대 — 종류별 신호 없음
        Assert.False(Has(row, "vlm_confidence"));
        Assert.False(Has(row, "ml_label"));
        Assert.True(row.GetProperty("vlm_only").GetBoolean());
        Assert.False(Has(row, "posture"));
    }

    [Fact]
    public async Task MetricsRow_ForAShakyPhoto_MarksUncertain()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 3);

        await Client(factory, new ScriptedDetector(() => Boxes(0.95, 0.9, 0.55, 0.45))).PostAsync("/api/u-vision/v2/inspect", Form());

        var row = Assert.Single(MetricsRows(factory));
        Assert.Equal("NG", row.GetProperty("verdict").GetString());
        Assert.True(row.GetProperty("count").GetProperty("uncertain").GetBoolean());
    }

    [Fact]
    public async Task MetricsRow_ForAComposition_NamesTheMissingAndExtraKinds()
    {
        using var factory = new UVisionApiFactory();
        SeedComposition(factory);
        // scalpel 은 기대 2 에 1개(모자람), forceps 는 기대 1 에 2개(과잉), 기대에 없는 clamp 1개(과잉).
        var detector = new ScriptedDetector(() =>
            Tray(("scalpel", 0.95), ("forceps", 0.9), ("forceps", 0.88), ("clamp", 0.9)));

        await Client(factory, detector).PostAsync("/api/u-vision/v2/inspect", Form());

        var count = Assert.Single(MetricsRows(factory)).GetProperty("count");
        Assert.Equal(["scalpel"], count.GetProperty("missing").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["clamp", "forceps"], count.GetProperty("extra").EnumerateArray().Select(e => e.GetString()).Order());
    }

    [Fact]
    public async Task MetricsRow_ForAComposition_AsExpected_HasEmptyArrays_SoItStillCountsAsAComposition()
    {
        using var factory = new UVisionApiFactory();
        SeedComposition(factory);

        await Client(factory, new ScriptedDetector(() => Tray(("scalpel", 0.95), ("scalpel", 0.9), ("forceps", 0.85))))
            .PostAsync("/api/u-vision/v2/inspect", Form());

        var count = Assert.Single(MetricsRows(factory)).GetProperty("count");
        Assert.Equal(0, count.GetProperty("missing").GetArrayLength());
        Assert.Equal(0, count.GetProperty("extra").GetArrayLength());
    }

    [Fact]
    public async Task DetectorDown_WritesAnUnavailableCountRow_NotAFailClosedOne()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 3);

        await Client(factory, new ThrowingDetector()).PostAsync("/api/u-vision/v2/inspect", Form());

        // «검출기 불능» 은 «VLM 판정 불가(fail_closed)» 가 아니다 — 행은 있되 fail_closed 자세 · verdict 가 없고 표지만 불능이다.
        var row = Assert.Single(MetricsRows(factory));
        Assert.True(row.GetProperty("count").GetProperty("unavailable").GetBoolean());
        Assert.False(Has(row, "verdict"));
        Assert.False(Has(row, "posture"));
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

    // ── 구성 확인(종류별 기대) ────────────────────────────────────────────

    private static void SeedComposition(UVisionApiFactory factory, string? extraPolicy = null, string[]? kinds = null)
    {
        factory.CreateClient();
        factory.SeedScenario(new Scenario
        {
            ScenarioId = ScenarioId,
            Name = "수술 트레이",
            Type = "counting",
            ExpectedByClass = new Dictionary<string, int> { ["scalpel"] = 2, ["forceps"] = 1 },
            ExtraPolicy = extraPolicy,
            CountClasses = kinds is null ? null : [.. kinds],
        });
    }

    private static DetectionSet Tray(params (string Kind, double Confidence)[] boxes) =>
        new([.. boxes.Select(b => Box(b.Confidence, b.Kind))], "sha-abc");

    [Fact]
    public async Task Composition_AsExpected_IsOk_WithItems_AndWithoutCallingTheVlm()
    {
        using var factory = new UVisionApiFactory();
        SeedComposition(factory);
        var detector = new ScriptedDetector(() => Tray(("scalpel", 0.95), ("scalpel", 0.9), ("forceps", 0.85)));

        var resp = await Client(factory, detector).PostAsync("/api/u-vision/v2/inspect", Form());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OK", body.GetProperty("class").GetString());
        var count = body.GetProperty("count");
        Assert.Equal(3, count.GetProperty("expected").GetInt32()); // 기대 합계
        Assert.Equal(0, count.GetProperty("tolerance").GetInt32());
        Assert.Equal("ng", count.GetProperty("extra_policy").GetString());
        var items = count.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["forceps", "scalpel"], items.Select(i => i.GetProperty("class").GetString()));
        Assert.All(items, i => Assert.Equal("ok", i.GetProperty("status").GetString()));
        Assert.False(body.TryGetProperty("requires_review", out _));
    }

    [Fact]
    public async Task Composition_MissingKind_IsNg_V1ShowsFindingsNamingIt_AndTheRecordKeepsTheItems()
    {
        using var factory = new UVisionApiFactory();
        SeedComposition(factory);
        var client = Client(factory, new ScriptedDetector(() => Tray(("scalpel", 0.95), ("scalpel", 0.9))));

        var v1 = await client.PostAsync("/api/u-vision/inspect", Form());

        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        var body = await v1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NG", body.GetProperty("verdict").GetString());
        Assert.Contains("누락: forceps 1개 부족", body.GetProperty("findings").GetString());
        Assert.False(body.TryGetProperty("count", out _)); // v1 은 OK/NG 보기

        var record = StoredRecord(factory).GetProperty("count");
        var forceps = record.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("class").GetString() == "forceps");
        Assert.Equal("missing", forceps.GetProperty("status").GetString());
        Assert.Equal(0, forceps.GetProperty("found").GetInt32());
        var annotations = (await LedgerLineAsync(factory)).Provenance.Annotations!;
        Assert.Equal("1", annotations["count:missing"]);
        Assert.Equal("0", annotations["count:extra"]);
    }

    [Theory]
    [InlineData(null, "NG", true)]    // 기본은 과잉도 불량
    [InlineData("warn", "OK", true)]  // 항목에는 올리되 판정은 그대로
    [InlineData("ignore", "OK", false)]
    public async Task Composition_UnexpectedKind_FollowsTheExtraPolicy(string? policy, string expectedClass, bool listedAsExtra)
    {
        using var factory = new UVisionApiFactory();
        SeedComposition(factory, extraPolicy: policy);
        var detector = new ScriptedDetector(() => Tray(("scalpel", 0.95), ("scalpel", 0.9), ("forceps", 0.85), ("lighter", 0.9)));

        var body = await (await Client(factory, detector).PostAsync("/api/u-vision/v2/inspect", Form()))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(expectedClass, body.GetProperty("class").GetString());
        var items = body.GetProperty("count").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(listedAsExtra, items.Any(i => i.GetProperty("class").GetString() == "lighter" && i.GetProperty("status").GetString() == "extra"));
        Assert.Equal(policy ?? "ng", body.GetProperty("count").GetProperty("extra_policy").GetString());
    }

    [Fact]
    public async Task Composition_BoxNearTheThreshold_ThatSavesAMissingKind_IsNg_WithReviewBlock()
    {
        using var factory = new UVisionApiFactory();
        SeedComposition(factory);
        // forceps 는 문턱 바로 위(0.52) 한 개뿐 — 지금은 맞지만 그 상자가 없으면 누락이다.
        var detector = new ScriptedDetector(() => Tray(("scalpel", 0.95), ("scalpel", 0.9), ("forceps", 0.52)));

        var body = await (await Client(factory, detector).PostAsync("/api/u-vision/v2/inspect", Form()))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("NG", body.GetProperty("class").GetString());
        Assert.True(body.GetProperty("requires_review").GetBoolean());
        Assert.Equal("review_block", body.GetProperty("posture").GetString());
        Assert.True(body.GetProperty("count").GetProperty("uncertain").GetBoolean());
    }

    [Fact]
    public async Task TotalCounting_NeverGetsItemsOrAnExtraPolicy_SoItsWireIsUnchanged()
    {
        using var factory = new UVisionApiFactory();
        Seed(factory, expected: 2);
        var detector = new ScriptedDetector(() => Boxes(0.95, 0.9));

        var body = await (await Client(factory, detector).PostAsync("/api/u-vision/v2/inspect", Form()))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("count").TryGetProperty("items", out _));
        Assert.False(body.GetProperty("count").TryGetProperty("extra_policy", out _));
        Assert.False(StoredRecord(factory).GetProperty("count").TryGetProperty("items", out _));
        var annotations = (await LedgerLineAsync(factory)).Provenance.Annotations!;
        Assert.False(annotations.ContainsKey("count:missing"));
    }
}
