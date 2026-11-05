using Microsoft.Extensions.Logging.Abstractions;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Counting;
using UVision.Api.Services.Inspection;
using UVision.Api.Services.Ml;
using UVision.Api.Services.Scenarios;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 결정 장부가 ML 단에 적는 판 — 신뢰 장부가 «어느 모델의 증거인가» 로 칸을 가르는 키다. 레지스트리에 등록한 판이 있으면 그것,
/// 없으면 서빙 쪽이 답했다고 보고한 모델 파일 해시, 그것도 없으면 분류기 이름. 확정 레코드의 <c>tier_version</c> 은 ML 이 확정할 때만
/// 이 값을 담고 지금은 ML 단이 Shadow 라 거기서는 보이지 않으므로, 단 목록을 직접 읽어 고정한다.
/// </summary>
public class DecisionLedgerTierVersionTests
{
    private sealed class NamedClassifier(string name) : IMlClassifier
    {
        public string Name => name;
        public bool IsEnabled => true;

        public Task<MlClassification> ClassifyAsync(
            ReadOnlyMemory<byte> image, string scenarioId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("장부는 판정원을 다시 부르지 않는다");
    }

    private static string MlTierVersion(MlClassification? answer)
    {
        var root = Path.Combine(Path.GetTempPath(), "uvision-ledger-tier-" + Guid.NewGuid().ToString("N"));
        var ledger = new DecisionLedger(
            new StoragePaths(new StorageOptions { DataPath = root }, AppContext.BaseDirectory),
            new MockVlmProvider(),
            new NamedClassifier("mloop"),
            ObjectDetectorFactory.Create(new DetectionOptions()),
            new VlmOptions(),
            NullLogger<DecisionLedger>.Instance);
        var scenario = EffectiveScenario.Resolve(new Scenario { ScenarioId = "demo", Name = "x" }, mlEnabled: true);
        var subject = new LedgerSubject(
            "img_1", scenario, "2026-11-05T00:00:00.0000000Z", Vlm: null,
            Ml: answer is null ? MlOutcome.Failure("down") : MlOutcome.Success(answer));

        var tier = ledger.Judges(subject).Select(j => j.Tier).Single(t => t.Id == ScenarioVocabulary.MlTier);
        return tier.Version!;
    }

    private static MlClassification Answer(string? registryVersion = null, string? sha = null) => new()
    {
        Label = "ok", Confidence = 0.9, ModelVersion = registryVersion, ModelSha256 = sha,
    };

    [Fact]
    public void MlTierVersion_PrefersTheRegistryVersion_ThenTheServedModelHash_ThenTheClassifierName()
    {
        Assert.Equal("v3", MlTierVersion(Answer(registryVersion: "v3", sha: "sha-served")));
        Assert.Equal("sha-served", MlTierVersion(Answer(sha: "sha-served")));
        Assert.Equal("mloop", MlTierVersion(Answer()));
    }

    [Fact]
    public void MlTierVersion_FallsBackToTheClassifierName_WhenTheClassificationFailed()
    {
        // 실패한 단은 답이 없으니 판을 알 수 없다 — 이름으로 남는다.
        Assert.Equal("mloop", MlTierVersion(null));
    }
}
