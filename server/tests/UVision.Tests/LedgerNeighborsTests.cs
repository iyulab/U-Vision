extern alias bench;

using bench::UVision.Benchmark;
using UVision.Api.Services.Memory;
using Xunit;

namespace UVision.Tests;

/// <summary>장부로 이웃 수 k 다시 세기 — 같은 기억 · 같은 질의를 다른 k 로 직접 판정한 것과 같은 수가 나와야 한다.</summary>
public class LedgerNeighborsTests
{
    private static float[] V(params float[] v) => VectorMath.Normalize(v);

    private static List<MemoryCase> Memory()
    {
        var m = new List<MemoryCase>();
        for (var i = 0; i < 12; i++) m.Add(new MemoryCase($"ok{i}", "OK", V(1f, 0.02f * (i + 1), 0f)));
        m.Add(new MemoryCase("ng-near", "NG", V(1f, 0.07f, 0f)));
        for (var i = 0; i < 8; i++) m.Add(new MemoryCase($"ng{i}", "NG", V(0.3f, 1f, 0.01f * i)));
        return m;
    }

    [Fact]
    public void Recount_MatchesJudgingAgainWithEachK_ForQueriesOfBothClasses()
    {
        var memory = Memory();
        // (질의, 사람 라벨) — OK 쪽 · NG 쪽 · NG 인데 OK 이웃에 둘러싸인 것(놓침 후보)
        (float[] Query, string Label)[] queries =
        [
            (V(1f, 0.01f, 0f), "OK"), (V(1f, 0.03f, 0f), "OK"), (V(1f, 0.065f, 0f), "NG"), (V(0.3f, 1f, 0.02f), "NG"), (V(0.31f, 1f, 0.05f), "NG"),
        ];
        var cases = queries
            .Select(q => new LedgerNeighbors.Case(MemoryJudge.Decide(q.Query, memory, neighbors: 5, maxDistance: 0.5).ToAnnotations(), q.Label))
            .ToList();

        foreach (var row in LedgerNeighbors.Recount(cases, [1, 3, 5, 7, 9], maxDistance: 0.5))
        {
            var direct = queries.Select(q => (Answer: MemoryJudge.Decide(q.Query, memory, row.K, 0.5), q.Label)).ToList();
            var answered = direct.Where(d => d.Answer.Class is not null).ToList();
            Assert.Equal(answered.Count, row.Answered);
            Assert.Equal(answered.Count(d => string.Equals(d.Answer.Class, d.Label, StringComparison.OrdinalIgnoreCase)), row.Agreed);
            Assert.Equal(answered.Count(d => d.Label == "NG" && d.Answer.Class == "OK"), row.Missed);
        }
    }

    [Fact]
    public void Recount_SkipsLinesItCannotRecount_InsteadOfCountingThemAsAnswers()
    {
        var oldLine = new Dictionary<string, string> { ["memory:neighbors"] = "5", ["memory:agreed_class"] = "OK", ["memory:nearest_distance"] = "0.01" };
        var rows = LedgerNeighbors.Recount([new LedgerNeighbors.Case(oldLine, "NG")], [5], maxDistance: 0.15);
        Assert.Equal(new LedgerNeighbors.Row(5, 0, 0, 0), rows.Single());
    }
}
