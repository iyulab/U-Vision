using UVision.Api.Services.Memory;
using UVision.Api.Services.Scenarios;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>기억 단 문턱 추천 — 재생 표에서 «놓침 0 인 가장 큰 문턱» 과 그 증거(불량 통과율의 95 % 상한)를 낸다.</summary>
public class OptionAdvisorTests
{
    private static readonly EffectiveScenario Inspection =
        EffectiveScenario.Resolve(new Scenario { ScenarioId = "s", Name = "s" }, mlEnabled: false, memoryEnabled: true);

    private static IReadOnlyList<MemoryReplayRow> Replay(IReadOnlyList<ReplayCase> cases) =>
        MemoryReplay.Run(cases, Inspection, configured: 0.15);

    private static IEnumerable<ReplayCase> Defects(int count) =>
        Enumerable.Range(0, count).Select(_ => new ReplayCase(null, 0.5, null, "NG"));   // 이웃이 갈려 답하지 않은 불량

    [Fact]
    public void RecommendsLargestThresholdWithoutMiss_AndReportsEvidence()
    {
        // OK 10 장은 0.02 에서 답함. 불량 하나는 0.12 에서 OK 로 답함 — 0.15 부터 놓침. 나머지 불량 5 는 답하지 않음.
        var cases = Enumerable.Range(0, 10).Select(_ => new ReplayCase("OK", 0.02, "OK", "OK"))
            .Append(new ReplayCase("OK", 0.12, "OK", "NG"))
            .Concat(Defects(5))
            .ToList();

        var advice = OptionAdvisor.AdviseMaxDistance(Replay(cases), targetMissRate: 0.02);

        Assert.Equal(0.10, advice.MaxDistance);
        Assert.Equal((10, 6, 0), (advice.Answered, advice.LabeledDefects, advice.Missed));
        // 0 / 6 에서 불량 통과율의 95 % 상한 = 1 − 0.05^(1/6)
        Assert.Equal(1 - Math.Pow(0.05, 1.0 / 6), advice.DefectPassUpper95!.Value, 6);
        Assert.False(advice.Sufficient);
        Assert.Equal(149, advice.DefectsNeeded);
    }

    [Fact]
    public void Sufficient_OnlyWhenTheUpperBoundReachesTheTarget()
    {
        // 목표 2 % 를 95 % 신뢰로 주장하려면 놓침 0 에서도 불량 149 건이 필요하다(148 건은 상한이 2.004 %).
        var ok = Enumerable.Range(0, 20).Select(_ => new ReplayCase("OK", 0.02, null, "OK"));

        var at149 = OptionAdvisor.AdviseMaxDistance(Replay([.. ok, .. Defects(149)]), targetMissRate: 0.02);
        var at148 = OptionAdvisor.AdviseMaxDistance(Replay([.. ok, .. Defects(148)]), targetMissRate: 0.02);

        Assert.True(at149.Sufficient);
        Assert.False(at148.Sufficient);
        Assert.True(at149.DefectPassUpper95 <= 0.02 && at148.DefectPassUpper95 > 0.02);
    }

    [Fact]
    public void LadderEndWithoutMiss_RecommendsTheLargestRung_AndSaysWhatWasNotMeasured()
    {
        var cases = Enumerable.Range(0, 5).Select(_ => new ReplayCase("OK", 0.25, null, "OK")).Concat(Defects(3)).ToList();

        var advice = OptionAdvisor.AdviseMaxDistance(Replay(cases), targetMissRate: 0.02);

        Assert.Equal(0.30, advice.MaxDistance);
        Assert.Contains("사다리", advice.Reason);
    }

    [Fact]
    public void NoLabeledDefects_HasNoBasis()
    {
        var cases = Enumerable.Range(0, 10).Select(_ => new ReplayCase("OK", 0.02, null, "OK")).ToList();

        var advice = OptionAdvisor.AdviseMaxDistance(Replay(cases), targetMissRate: 0.02);

        Assert.Null(advice.MaxDistance);
        Assert.Null(advice.DefectPassUpper95);
        Assert.False(advice.Sufficient);
        Assert.Contains("불량 라벨", advice.Reason);
    }

    [Fact]
    public void EveryRungMisses_NothingToRecommend()
    {
        // 불량이 0.005 에서 OK 로 답했다 — 사다리의 가장 작은 문턱(0.01)에서도 이미 놓친다.
        var cases = new List<ReplayCase> { new("OK", 0.005, null, "NG") };
        cases.AddRange(Defects(3));

        var advice = OptionAdvisor.AdviseMaxDistance(Replay(cases), targetMissRate: 0.02);

        Assert.Null(advice.MaxDistance);
        Assert.Contains("모든 문턱", advice.Reason);
    }

    [Fact]
    public void NothingAnswered_NothingToRecommend()
    {
        var advice = OptionAdvisor.AdviseMaxDistance(Replay(Defects(5).ToList()), targetMissRate: 0.02);

        Assert.Null(advice.MaxDistance);
        Assert.Contains("답한", advice.Reason);
    }

    [Fact]
    public void EmptyReplay_HasNoBasis()
    {
        var advice = OptionAdvisor.AdviseMaxDistance([], targetMissRate: 0.02);

        Assert.Null(advice.MaxDistance);
        Assert.Equal(0, advice.Answered);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    public void TargetMissRateOutsideOpenUnitInterval_IsRejected(double target) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OptionAdvisor.AdviseMaxDistance([], target));
}
