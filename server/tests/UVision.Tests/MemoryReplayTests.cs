using UVision.Api.Models;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Scenarios;
using Xunit;

namespace UVision.Tests;

/// <summary>기억 문턱 재생 — 문턱마다 답한 수 · 교사 일치 · 라벨 일치 · 놓침.</summary>
public class MemoryReplayTests
{
    private static readonly EffectiveScenario Inspection =
        EffectiveScenario.Resolve(new Scenario { ScenarioId = "s", Name = "s" }, mlEnabled: false, memoryEnabled: true);

    [Fact]
    public void CountsPerThreshold_AgainstTeacherAndHumanLabel()
    {
        ReplayCase[] cases =
        [
            new("OK", 0.02, "OK", "OK"),   // 가까움 · 모두 일치
            new("OK", 0.12, "OK", "NG"),   // 0.15 에서 답함 — 사람은 불량: 놓침
            new("NG", 0.25, "NG", null),   // 0.30 에서만 답함 · 라벨 없음
            new(null, 0.01, "OK", "OK"),   // 이웃이 갈림 — 어느 문턱에서도 답하지 않음
        ];

        var rows = MemoryReplay.Run(cases, Inspection, configured: 0.15);

        Assert.Equal([0.05, 0.10, 0.15, 0.20, 0.30], rows.Select(r => r.MaxDistance));
        var at05 = rows[0];
        Assert.Equal((1, 1.0, 1, 1.0, 0, 0.0), (at05.Answered, at05.TeacherAgreement, at05.Labeled, at05.LabelAgreement, at05.Missed, at05.MissRate));
        var at15 = rows.Single(r => r.Configured);
        Assert.Equal(0.15, at15.MaxDistance);
        Assert.Equal((2, 1.0, 2, 0.5, 1, 0.5), (at15.Answered, at15.TeacherAgreement, at15.Labeled, at15.LabelAgreement, at15.Missed, at15.MissRate));
        var at30 = rows[^1];
        Assert.Equal((3, 3, 2), (at30.Answered, at30.TeacherChecked, at30.Labeled));
    }

    [Fact]
    public void ConfiguredThresholdOffTheLadder_IsAddedInOrder_AndEmptyRatesAreNull()
    {
        var rows = MemoryReplay.Run([new("OK", 0.5, null, null)], Inspection, configured: 0.12);

        Assert.Equal([0.05, 0.10, 0.12, 0.15, 0.20, 0.30], rows.Select(r => r.MaxDistance));
        Assert.True(rows[2].Configured);
        Assert.All(rows, r => Assert.Equal((0, (double?)null, (double?)null, (double?)null), (r.Answered, r.TeacherAgreement, r.LabelAgreement, r.MissRate)));
    }

    [Fact]
    public void FromAnnotations_ReadsDistanceAgreedClass_AndTeacherFromOperative()
    {
        var c = MemoryReplay.FromAnnotations(new Dictionary<string, string>
        {
            ["memory:nearest_distance"] = "0.08",
            ["memory:agreed_class"] = "OK",
            ["operative:verdict"] = "NG",
        }, Inspection, "NG");

        Assert.Equal(new ReplayCase("OK", 0.08, "NG", "NG"), c);
        Assert.Null(MemoryReplay.FromAnnotations(new Dictionary<string, string> { ["operative:verdict"] = "OK" }, Inspection, null));
        Assert.Null(MemoryReplay.FromAnnotations(new Dictionary<string, string>
        {
            ["memory:nearest_distance"] = "0.1", ["operative:verdict"] = "none",
        }, Inspection, null)!.TeacherClass);
    }

    [Fact]
    public void Judge_RecordsAgreedClass_EvenWhenBeyondThreshold()
    {
        var far = VectorMath.Normalize([0, 0, 1]);
        var memory = new[] { new MemoryCase("a", "OK", VectorMath.Normalize([1, 0, 0])) };

        var answer = MemoryJudge.Decide(far, memory, neighbors: 1, maxDistance: 0.15);

        Assert.Null(answer.Class);
        Assert.Equal("OK", answer.AgreedClass);
        Assert.Equal("OK", answer.ToAnnotations()["memory:agreed_class"]);
    }
}
