using UVision.Api.Models;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Scenarios;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 플라이휠 곡선 — 사람 확정 라벨이 쌓이는 순서대로 «실제 판정» 과 «기억 단» 이 사람 라벨과 얼마나 맞았나. 라벨이 있는 판정만
/// 센다(정답을 모르는 건은 정확도를 말할 수 없다). 곡선 없이 «쓸수록 나아진다» 를 주장하지 않기 위한 지표다.
/// </summary>
public class FlywheelCurveTests
{
    private static readonly EffectiveScenario Inspection =
        EffectiveScenario.Resolve(new Scenario { ScenarioId = "s", Name = "s" }, mlEnabled: false, memoryEnabled: true);

    private static CurveCase Case(string? operative, string? agreed, double? distance, string human) =>
        new(operative, agreed, distance, human);

    [Fact]
    public void SplitsLabeledCasesIntoBuckets_InOrder_WithTheLastBucketPartial()
    {
        var cases = Enumerable.Range(0, 5).Select(_ => Case("OK", null, null, "OK")).ToList();

        var curve = FlywheelCurve.Build(cases, Inspection, bucketSize: 2, memoryMaxDistance: 0.15);

        Assert.Equal([2, 4, 5], curve.Select(p => p.LabeledThrough));
        Assert.Equal([2, 2, 1], curve.Select(p => p.Judged));
    }

    [Fact]
    public void OperativeAgreement_AndDefectPass_AreCountedAgainstTheHumanLabel()
    {
        // 한 버킷(4건): 사람 OK 2 · NG 2. 판정은 OK→OK, OK→OK, NG→NG, 불량을 OK 로 지나감(NG 사람 라벨 / OK 판정).
        var cases = new[]
        {
            Case("OK", null, null, "OK"), Case("OK", null, null, "OK"),
            Case("NG", null, null, "NG"), Case("OK", null, null, "NG"),
        };

        var p = Assert.Single(FlywheelCurve.Build(cases, Inspection, bucketSize: 4, memoryMaxDistance: 0.15));

        Assert.Equal(0.75, p.OperativeAgreement);
        Assert.Equal((2, 1, 0.5), (p.LabeledDefects, p.OperativeMissed, p.OperativeDefectPass));
    }

    [Fact]
    public void Memory_AnswersOnlyInsideTheThreshold_AndItsMissesAreOkAnswersOnDefects()
    {
        var cases = new[]
        {
            Case("OK", "OK", 0.02, "OK"),    // 답함 · 일치
            Case("OK", "OK", 0.30, "OK"),    // 문턱 밖 — 답하지 않음
            Case("OK", "OK", 0.10, "NG"),    // 답함 · 불량을 OK 로 — 기억이 맡았다면 놓쳤을 것
            Case("OK", null, 0.01, "OK"),    // 이웃이 갈려 답하지 않음
        };

        var p = Assert.Single(FlywheelCurve.Build(cases, Inspection, bucketSize: 4, memoryMaxDistance: 0.15));

        Assert.Equal((2, 0.5), (p.MemoryAnswered, p.MemoryAnsweredRate));
        Assert.Equal(0.5, p.MemoryAgreement);
        Assert.Equal(1, p.MemoryMissed);
    }

    [Fact]
    public void RatesWithAnEmptyDenominator_AreNull_NotZero()
    {
        // 기억이 꺼진 장부(기억 annotation 없음) · 판정 클래스를 모르는 건.
        var p = Assert.Single(FlywheelCurve.Build([Case(null, null, null, "OK")], Inspection, bucketSize: 5, memoryMaxDistance: 0.15));

        Assert.Null(p.OperativeAgreement);
        Assert.Null(p.OperativeDefectPass);          // 사람 라벨 불량이 없다
        Assert.Equal(0, p.MemoryAnswered);
        Assert.Null(p.MemoryAgreement);
        Assert.Equal(0.0, p.MemoryAnsweredRate);     // 답한 비율은 분모가 판정 수라 0 이 맞다
    }

    [Fact]
    public void EmptyInput_GivesNoPoints_AndABucketSizeBelowOneIsRejected()
    {
        Assert.Empty(FlywheelCurve.Build([], Inspection, bucketSize: 10, memoryMaxDistance: 0.15));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlywheelCurve.Build([], Inspection, bucketSize: 0, memoryMaxDistance: 0.15));
    }

    [Fact]
    public void FromAnnotations_ReadsTheOperativeClassAndMemoryAnnotations_AndKeepsCasesWithoutMemory()
    {
        var withMemory = FlywheelCurve.FromAnnotations(new Dictionary<string, string>
        {
            ["operative:class"] = "OK",
            ["memory:agreed_class"] = "OK",
            ["memory:nearest_distance"] = "0.08",
        }, Inspection, "OK");
        Assert.Equal(new CurveCase("OK", "OK", 0.08, "OK"), withMemory);

        // 기억이 꺼졌던 시절의 줄도 «실제 판정» 곡선에는 든다 — 기억 칸만 비어 있다.
        var noMemory = FlywheelCurve.FromAnnotations(new Dictionary<string, string> { ["operative:verdict"] = "NG" }, Inspection, "NG");
        Assert.Equal(new CurveCase("NG", null, null, "NG"), noMemory);

        // 사람 라벨이 없으면 정답을 모른다 — 곡선에 넣지 않는다.
        Assert.Null(FlywheelCurve.FromAnnotations(new Dictionary<string, string> { ["operative:class"] = "OK" }, Inspection, null));
    }
}
