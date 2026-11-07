using UVision.Api.Models;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Scenarios;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 이웃 수 k 곡선 — 장부의 이웃 순서로 다시 센 값이 같은 기억 · 같은 질의를 k 로 직접 판정한 값과 같아야 하고,
/// 다시 셀 수 없는 줄을 «답함» 으로 세지 않아야 한다.
/// </summary>
public class NeighborCurveTests
{
    private static readonly EffectiveScenario Inspection =
        EffectiveScenario.Resolve(new Scenario { ScenarioId = "s", Name = "s" }, mlEnabled: false, memoryEnabled: true);

    private static float[] V(params float[] v) => VectorMath.Normalize(v);

    private static List<MemoryCase> Memory()
    {
        var m = new List<MemoryCase>();
        for (var i = 0; i < 12; i++) m.Add(new MemoryCase($"ok{i}", "OK", V(1f, 0.02f * (i + 1), 0f)));
        m.Add(new MemoryCase("ng-near", "NG", V(1f, 0.07f, 0f)));
        for (var i = 0; i < 8; i++) m.Add(new MemoryCase($"ng{i}", "NG", V(0.3f, 1f, 0.01f * i)));
        return m;
    }

    private static NeighborRecord Record(MemoryAnswer answer, string label) =>
        new(MemoryReplay.FromAnnotations(answer.ToAnnotations(), Inspection, label)!, answer.ToAnnotations());

    [Fact]
    public void Curve_MatchesJudgingAgainWithEachK_AndMarksOnlyTheConfiguredK()
    {
        var memory = Memory();
        // (질의, 사람 라벨) — OK 쪽 · NG 쪽 · NG 인데 OK 이웃에 둘러싸인 것(놓침 후보)
        (float[] Query, string Label)[] queries =
        [
            (V(1f, 0.01f, 0f), "OK"), (V(1f, 0.03f, 0f), "OK"), (V(1f, 0.065f, 0f), "NG"), (V(0.3f, 1f, 0.02f), "NG"), (V(0.31f, 1f, 0.05f), "NG"),
        ];
        var records = queries.Select(q => Record(MemoryJudge.Decide(q.Query, memory, neighbors: 5, maxDistance: 0.5), q.Label)).ToList();

        var curve = NeighborCurve.Compute(records, Inspection, maxDistance: 0.5, configuredK: 5)!;

        Assert.Equal(0.5, curve.MaxDistance);
        Assert.Equal([1, 3, 5, 9], curve.Rows.Select(r => r.K)); // 7 은 격자에 없다 — 설정된 k 가 격자 밖이면 더해 보인다(아래 시험)
        Assert.Equal([5], curve.Rows.Where(r => r.Configured).Select(r => r.K));
        foreach (var row in curve.Rows)
        {
            var direct = queries.Select(q => (Answer: MemoryJudge.Decide(q.Query, memory, row.K, 0.5), q.Label)).ToList();
            var answered = direct.Count(d => d.Answer.Class is not null);
            Assert.Equal(queries.Length, row.Counted);
            Assert.Equal(answered, row.Answered);
            Assert.Equal(Math.Round((double)answered / queries.Length, 4), row.AnswerRate);
            Assert.Equal(queries.Count(q => q.Label == "NG"), row.LabeledDefects);
            Assert.Equal(direct.Count(d => d.Label == "NG" && d.Answer.Class == "OK"), row.Missed);
        }
    }

    [Fact]
    public void ConfiguredK_OutsideTheGrid_IsAddedInOrder()
    {
        var memory = Memory();
        var record = Record(MemoryJudge.Decide(V(1f, 0.01f, 0f), memory, neighbors: 7, maxDistance: 0.5), "OK");

        var curve = NeighborCurve.Compute([record], Inspection, 0.5, configuredK: 7)!;

        Assert.Equal([1, 3, 5, 7, 9], curve.Rows.Select(r => r.K));
        Assert.Equal(7, curve.Rows.Single(r => r.Configured).K);
    }

    [Fact]
    public void Lines_ThatCannotBeRecounted_AreLeftOut_NotCountedAsAnswers()
    {
        // 기록 경로가 생기기 전의 줄 — 이웃 순서 키가 없다
        var oldLine = new Dictionary<string, string> { ["memory:neighbors"] = "5", ["memory:agreed_class"] = "OK", ["memory:nearest_distance"] = "0.01" };
        var old = new NeighborRecord(MemoryReplay.FromAnnotations(oldLine, Inspection, "NG")!, oldLine);
        Assert.Null(NeighborCurve.Compute([old], Inspection, 0.15, configuredK: 5));

        // 기억이 사례 셋뿐이라 이웃 순서도 셋 — k 5 · 9 는 다시 셀 수 없어 행이 없다
        var small = Memory().Take(3).ToList();
        var curve = NeighborCurve.Compute(
            [Record(MemoryJudge.Decide(V(1f, 0.01f, 0f), small, neighbors: 1, maxDistance: 0.5), "OK"), old],
            Inspection, 0.5, configuredK: 1)!;
        Assert.Equal([1, 3], curve.Rows.Select(r => r.K));
        Assert.All(curve.Rows, r => Assert.Equal(1, r.Counted)); // 옛 줄은 어느 k 에서도 세지 않았다
    }

    [Fact]
    public void DefectPassRate_IsOmittedWithoutDefectLabels_AndShowsTheTradeOffWithThem()
    {
        var memory = Memory();
        var onlyOk = NeighborCurve.Compute(
            [Record(MemoryJudge.Decide(V(1f, 0.01f, 0f), memory, 5, 0.5), "OK")], Inspection, 0.5, 5)!;
        Assert.All(onlyOk.Rows, r => Assert.Null(r.DefectPassRate));

        // 불량으로 확정된 사진이 가까운 OK 이웃 셋에 둘러싸였고 네 번째 이웃만 NG: k 1 · 3 에서는 OK 로 답해 지나가고,
        // k 5 부터는 NG 이웃이 섞여 이웃이 갈리므로 기권한다 — 놓침은 줄고 응답률도 함께 0 으로 떨어진다(맞교환).
        var ng = NeighborCurve.Compute(
            [Record(MemoryJudge.Decide(V(1f, 0.03f, 0f), memory, 9, 0.5), "NG")], Inspection, 0.5, 5)!;
        Assert.Equal([1, 1, 0, 0], ng.Rows.Select(r => r.Missed));
        Assert.Equal([1.0, 1.0, 0.0, 0.0], ng.Rows.Select(r => r.DefectPassRate!.Value));
        Assert.Equal([1.0, 1.0, 0.0, 0.0], ng.Rows.Select(r => r.AnswerRate));
    }
}
