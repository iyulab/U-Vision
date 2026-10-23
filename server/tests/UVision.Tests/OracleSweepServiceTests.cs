using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Oracle;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace UVision.Tests;

public class OracleSweepServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvsweep_" + Guid.NewGuid().ToString("N"));
    private StoragePaths Paths() => new(new StorageOptions { DataPath = _root }, _root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    // verdict 고정 fake 오라클.
    private sealed class FakeOracle(Verdict verdict, bool isCloud = false) : IOracleProvider
    {
        public int Calls;
        public string Name => "fake";
        public bool IsEnabled => true;
        public bool IsCloud => isCloud;
        public Task<InspectionResult> SecondOpinionAsync(ReadOnlyMemory<byte> i, ScenarioContext s, CancellationToken c)
        { Calls++; return Task.FromResult(new InspectionResult { Verdict = verdict, Findings = "", Confidence = 0.95 }); }
    }

    private async Task<string> SeedReviewResultAsync(StoragePaths paths, string name, string date, string imageId)
    {
        var scenarioStore = new FileScenarioStore(paths);
        var scenario = await scenarioStore.CreateAsync(new ScenarioInput { Name = name }, default);
        var scenarioId = scenario.ScenarioId; // slug 도출 결과 id 사용

        var store = new FileInspectionStore(paths);
        var stored = new StoredResult
        {
            ScenarioId = scenarioId, ImageId = imageId, Verdict = Verdict.OK, Findings = "", Confidence = 0.9,
            Timestamp = $"{date}T00:00:00.000Z", ImageFile = imageId + ".jpg",
            DeviceId = "", DeviceLabel = "", RequiresReview = true,
        };
        await store.SaveAsync(new byte[] { 1, 2, 3 }, ".jpg", stored, default);
        return scenarioId;
    }

    private OracleSweepService Service(StoragePaths paths, IOracleProvider oracle, OracleOptions? opt = null) =>
        new(oracle, new FileScenarioStore(paths), new FileInspectionStore(paths),
            new FileReferenceStore(paths), new FileLabelStore(paths),
            opt ?? new OracleOptions { Provider = "openai-compatible", BatchCap = 10, LookbackDays = 2 },
            NullLoggerFactory.Instance);

    [Fact]
    public async Task Sweep_AppendsOracleLabel_OnReviewResult()
    {
        var paths = Paths();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var scenarioId = await SeedReviewResultAsync(paths, "sc", today, "img1");
        var oracle = new FakeOracle(Verdict.NG);

        var n = await Service(paths, oracle).SweepOnceAsync(default);

        Assert.Equal(1, n);
        var l = await new FileLabelStore(paths).ReadAsync(scenarioId, today, "img1");
        Assert.Contains(l!.History!, e => e.Mode == LabelMode.Oracle && e.Label == "NG");
        Assert.Null(l.OperativeLabel); // 미라벨 — operative 미변경
    }

    [Fact]
    public async Task Sweep_Idempotent_SecondRunNoDuplicate()
    {
        var paths = Paths();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await SeedReviewResultAsync(paths, "sc", today, "img1");
        var oracle = new FakeOracle(Verdict.NG);
        var svc = Service(paths, oracle);

        await svc.SweepOnceAsync(default);
        var n2 = await svc.SweepOnceAsync(default);

        Assert.Equal(0, n2);
        Assert.Equal(1, oracle.Calls);
    }

    [Fact]
    public async Task Sweep_CloudWithoutOptIn_Skips()
    {
        var paths = Paths();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await SeedReviewResultAsync(paths, "sc", today, "img1");
        var oracle = new FakeOracle(Verdict.NG, isCloud: true);

        var n = await Service(paths, oracle).SweepOnceAsync(default);

        Assert.Equal(0, n);          // allow_cloud_egress 기본 false → 차단
        Assert.Equal(0, oracle.Calls);
    }

    [Fact]
    public async Task Sweep_DegradeOnOracleThrow_Continues()
    {
        var paths = Paths();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await SeedReviewResultAsync(paths, "sc", today, "img1");

        // oracle that always throws
        var throwingOracle = new ThrowingOracle();
        var n = await Service(paths, throwingOracle).SweepOnceAsync(default);

        Assert.Equal(0, n); // 예외로 처리 실패 — 카운트 안 됨
        // 사이드카 없음 — 오라클 append 미발생
        var l = await new FileLabelStore(paths).ReadAsync(
            (await new FileScenarioStore(paths).ListAsync(default))[0].ScenarioId, today, "img1");
        Assert.Null(l);
    }

    private sealed class ThrowingOracle : IOracleProvider
    {
        public string Name => "throwing";
        public bool IsEnabled => true;
        public bool IsCloud => false;
        public Task<InspectionResult> SecondOpinionAsync(ReadOnlyMemory<byte> i, ScenarioContext s, CancellationToken c)
            => throw new InvalidOperationException("테스트 오라클 오류");
    }

    // --- BatchCap order-bias 회전(④-B 후속) ---

    [Theory]
    [InlineData(0, new[] { "a", "b", "c" })]
    [InlineData(1, new[] { "b", "c", "a" })]
    [InlineData(2, new[] { "c", "a", "b" })]
    [InlineData(3, new[] { "a", "b", "c" })]   // 한 바퀴
    [InlineData(-1, new[] { "c", "a", "b" })]  // 음수 안전
    public void Rotated_RoundRobinStart(int offset, string[] expected)
    {
        var rotated = OracleSweepPlanner.Rotated(new[] { "a", "b", "c" }, offset);
        Assert.Equal(expected, rotated);
    }

    [Fact]
    public void Rotated_EmptyOrSingle_Unchanged()
    {
        Assert.Equal(new[] { "x" }, OracleSweepPlanner.Rotated(new[] { "x" }, 5));
        Assert.Empty(OracleSweepPlanner.Rotated(Array.Empty<string>(), 5));
    }

    [Fact]
    public async Task Sweep_RotatesStart_LaterScenarioNotStarvedByBusyEarlier()
    {
        // s0(검토 2건)·s1(검토 1건), BatchCap=1. 고정 순서면 s1 은 s0 백로그(2건) 뒤라 3틱째에야 처리.
        // 회전이면 2틱째에 s1 이 시작점이 되어 처리된다 — 공정성 회복.
        var paths = Paths();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var s0 = await SeedReviewResultAsync(paths, "sc-a", today, "a1");
        await AddReviewResultAsync(paths, s0, today, "a2");
        var s1 = await SeedReviewResultAsync(paths, "sc-b", today, "b1");
        var svc = Service(paths, new FakeOracle(Verdict.NG), new OracleOptions { Provider = "openai-compatible", BatchCap = 1, LookbackDays = 1 });

        await svc.SweepOnceAsync(default); // tick0: 시작 s0 → a1
        await svc.SweepOnceAsync(default); // tick1: 회전 시작 s1 → b1

        var b1 = await new FileLabelStore(paths).ReadAsync(s1, today, "b1");
        Assert.Contains(b1!.History!, e => e.Mode == LabelMode.Oracle); // 2틱 내 s1 처리됨(기아 아님)
    }

    private static async Task AddReviewResultAsync(StoragePaths paths, string scenarioId, string date, string imageId)
    {
        var store = new FileInspectionStore(paths);
        var stored = new StoredResult
        {
            ScenarioId = scenarioId, ImageId = imageId, Verdict = Verdict.OK, Findings = "", Confidence = 0.9,
            Timestamp = $"{date}T00:00:00.000Z", ImageFile = imageId + ".jpg",
            DeviceId = "", DeviceLabel = "", RequiresReview = true,
        };
        await store.SaveAsync(new byte[] { 1, 2, 3 }, ".jpg", stored, default);
    }
}
