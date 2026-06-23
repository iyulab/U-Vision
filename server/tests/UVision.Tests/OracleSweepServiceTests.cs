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
            opt ?? new OracleOptions { Provider = "gpustack", BatchCap = 10, LookbackDays = 2 },
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
}
