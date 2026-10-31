using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using UVision.Api.Auth;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Oracle;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 계수의 OK/NG 는 «개수가 허용 범위 안이냐» 이지 사진의 불량이 아니다 — 사진의 불량을 가르는 소비자(오라클 2차 의견 VLM ·
/// 이미지 분류 학습 데이터셋)에 계수 레코드가 새면 엉뚱한 소견 · 엉뚱한 분류기가 생긴다. 둘 다 계수 시나리오를 거절한다.
/// </summary>
public class CountingExclusionTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;

    public CountingExclusionTests(PinFactory factory)
    {
        _factory = factory;
        factory.CreateClient();
        factory.SeedScenario(new Scenario { ScenarioId = "counted", Name = "계수", Type = "counting", ExpectedCount = 3 });
        factory.SeedScenario(new Scenario { ScenarioId = "plain", Name = "검사" });
    }

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        return client;
    }

    [Fact]
    public async Task Export_RefusesCountingScenario_WithAReason()
    {
        var resp = await Admin().PostAsync("/api/u-vision/datasets/export?scenario_id=counted", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("계수", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_RefusesCountingScenario()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/u-vision/datasets/readiness?scenario_id=counted");

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task OrdinaryScenario_StillExportsAndReportsReadiness()
    {
        Assert.Equal(HttpStatusCode.OK,
            (await _factory.CreateClient().GetAsync("/api/u-vision/datasets/readiness?scenario_id=plain")).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await Admin().PostAsync("/api/u-vision/datasets/export?scenario_id=plain", null)).StatusCode);
    }

    // ── 오라클 스윕 ───────────────────────────────────────────────────

    private sealed class CountingOracle : IOracleProvider
    {
        public int Calls;
        public string Name => "fake";
        public bool IsEnabled => true;
        public bool IsCloud => false;

        public Task<InspectionResult> SecondOpinionAsync(ReadOnlyMemory<byte> i, ScenarioContext s, CancellationToken c)
        {
            Calls++;
            return Task.FromResult(new InspectionResult { Verdict = Verdict.NG, Findings = "", Confidence = 0.95 });
        }
    }

    [Fact]
    public async Task OracleSweep_SkipsCountingScenario_ButStillSweepsOthers()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvsweep_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(new StorageOptions { DataPath = root }, root);
            var scenarios = new FileScenarioStore(paths);
            var store = new FileInspectionStore(paths);
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            foreach (var (name, input) in new[]
                     {
                         ("count", new ScenarioInput { Name = "count", Type = "counting", ExpectedCount = 3 }),
                         ("plain", new ScenarioInput { Name = "plain" }),
                     })
            {
                var id = (await scenarios.CreateAsync(input, default))!.ScenarioId;
                await store.SaveAsync(new byte[] { 1, 2, 3 }, ".jpg", new StoredResult
                {
                    ScenarioId = id, ImageId = "img_" + name, Verdict = Verdict.OK, Findings = "", Confidence = 0.9,
                    Timestamp = $"{today}T00:00:00.000Z", ImageFile = "img_" + name + ".jpg",
                    DeviceId = "", DeviceLabel = "", RequiresReview = true,
                }, default);
            }
            var oracle = new CountingOracle();
            var sweep = new OracleSweepService(
                oracle, scenarios, store, new FileReferenceStore(paths), new FileLabelStore(paths),
                new OracleOptions { Provider = "openai-compatible", BatchCap = 10, LookbackDays = 2 },
                NullLoggerFactory.Instance);

            var processed = await sweep.SweepOnceAsync(default);

            Assert.Equal(1, processed); // 계수 시나리오의 검토 대기 레코드는 건너뛰고 일반 시나리오만
            Assert.Equal(1, oracle.Calls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
