using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Ml;
using UVision.Api.Storage;

namespace UVision.Tests;

/// <summary>
/// 통합 테스트용 팩토리 — <c>Storage:DataPath</c> 를 격리된 temp 디렉토리로 덮고
/// <c>demo</c> 시나리오를 seed 한다. 저장소가 파일시스템이므로 테스트마다 깨끗한 루트가 필요하다.
/// <para>
/// 서브클래스(또는 <see cref="Create"/>)를 통해 ML provider·검토 임계·고정 신뢰도를 주입할 수 있다(A3).
/// xUnit IClassFixture 는 단일 public 생성자를 요구하므로 파라미터는 protected 필드로 노출한다.
/// </para>
/// </summary>
public class UVisionApiFactory : WebApplicationFactory<Program>
{
    static UVisionApiFactory()
    {
        // 테스트는 ambient .env(예: server/.env 의 gpustack 설정)에 의존하지 않는다 — provider 를
        // mock 으로 고정한다. Program 의 DotNetEnv 는 NoClobber 이므로 미리 설정한 이 값이 .env 보다
        // 우선한다. (호스트 빌드 전에 실행되도록 정적 생성자에 둔다.)
        Environment.SetEnvironmentVariable("VLM_PROVIDER", "mock");
    }

    /// <summary>ML provider 이름 override(null = none, VLM 단독).</summary>
    protected string? MlProvider { get; init; }

    /// <summary>검토 임계값 override(null = 기본 0.0).</summary>
    protected double? ReviewThreshold { get; init; }

    /// <summary>ML 분류기 고정 신뢰도(null = MockMlClassifier 결정론적 해시).</summary>
    protected double? MlConfidence { get; init; }

    public string DataPath { get; } =
        Path.Combine(Path.GetTempPath(), "uvision-tests-" + Guid.NewGuid().ToString("N"));

    private StoragePaths Paths =>
        new(new StorageOptions { DataPath = DataPath }, AppContext.BaseDirectory);

    /// <summary>
    /// A3 배선 검증용 팩토리를 생성한다(xUnit IClassFixture 와 달리 명시적 파라미터 전달).
    /// </summary>
    public static UVisionApiFactory Create(
        string? mlProvider = null,
        double? reviewThreshold = null,
        double? mlConfidence = null) =>
        new() { MlProvider = mlProvider, ReviewThreshold = reviewThreshold, MlConfidence = mlConfidence };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataPath);
        builder.UseSetting(
            $"{UVisionOptions.SectionName}:{StorageOptions.SectionName}:DataPath", DataPath);

        if (MlProvider is not null)
            builder.UseSetting($"{UVisionOptions.SectionName}:Ml:Provider", MlProvider);

        if (ReviewThreshold is not null)
            builder.UseSetting(
                $"{UVisionOptions.SectionName}:Ml:ReviewConfidenceThreshold",
                ReviewThreshold.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        // 고정 신뢰도: DI 등록 이후 override(ConfigureTestServices 는 ConfigureServices 다음에 실행).
        if (MlConfidence is not null)
        {
            var conf = MlConfidence.Value;
            builder.ConfigureTestServices(s =>
                s.AddSingleton<IMlClassifier>(new FixedConfidenceMlClassifier(conf)));
        }

        SeedScenario(new Scenario
        {
            ScenarioId = "demo",
            Name = "데모 검사",
            Criteria = "제품 표면에 외관 결함이 없어야 한다. 결함이 보이면 NG, 깨끗하면 OK.",
        });
    }

    /// <summary>시나리오 정의를 디스크에 직접 기록한다(테스트 준비용).</summary>
    public void SeedScenario(Scenario scenario)
    {
        var path = Paths.ScenarioJson(scenario.ScenarioId);
        StoragePaths.AtomicWriteJsonAsync(path, scenario).GetAwaiter().GetResult();
    }

    /// <summary>기준 이미지 1장을 디스크에 직접 기록한다(테스트 준비용 — 호스트 불필요).</summary>
    public void SeedReference(string scenarioId, ReferenceLabel label, ReadOnlyMemory<byte> image, string ext = ".jpg")
    {
        var refId = $"ref_{Guid.NewGuid():N}"[..12];
        var path = Paths.ReferenceFile(scenarioId, label, refId, ext);
        StoragePaths.AtomicWriteAsync(path, image).GetAwaiter().GetResult();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataPath))
            Directory.Delete(DataPath, recursive: true);
    }

    /// <summary>
    /// 고정 신뢰도를 반환하는 ML 분류기 — A3 배선 테스트에서 임계 게이팅을 결정론적으로 유발.
    /// "ok" 라벨을 반환해 VLM=OK 일 때 일치 상태를 보장하면서 신뢰도만 조정 가능.
    /// </summary>
    private sealed class FixedConfidenceMlClassifier : IMlClassifier
    {
        private readonly double _confidence;

        public FixedConfidenceMlClassifier(double confidence) => _confidence = confidence;

        public string Name => "fixed-confidence-mock";
        public bool IsEnabled => true;

        public Task<MlClassification> ClassifyAsync(
            ReadOnlyMemory<byte> image, string scenarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MlClassification
            {
                Label = "ok",
                Confidence = _confidence,
                Scores = new Dictionary<string, double> { ["ok"] = _confidence, ["ng"] = 1.0 - _confidence },
            });
    }
}
