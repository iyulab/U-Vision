using UVision.Api.Services.Memory;
using Xunit;

namespace UVision.Tests;

/// <summary>기억 단이 장부에 남기는 이웃 순서 — 다른 k 의 답을 장부에서 다시 셀 수 있어야 한다(옵션 추천 · 재생의 원천 자료).</summary>
public class MemoryNeighborRecordTests
{
    private static float[] V(params float[] v) => VectorMath.Normalize(v);

    // 두 클래스가 섞인 기억 — 질의 가까이에 OK 몇 장 사이로 NG 가 끼어 있어 k 에 따라 답이 갈린다.
    private static List<MemoryCase> MixedMemory()
    {
        var cases = new List<MemoryCase>();
        for (var i = 0; i < 12; i++)
            cases.Add(new MemoryCase($"ok{i}", "OK", V(1f, 0.02f * (i + 1), 0f)));
        cases.Add(new MemoryCase("ng-near", "NG", V(1f, 0.07f, 0f)));   // 4~5 번째로 가까운 곳에 NG
        for (var i = 0; i < 6; i++)
            cases.Add(new MemoryCase($"ng{i}", "NG", V(0.3f, 1f, 0.01f * i)));
        return cases;
    }

    [Fact]
    public void Decide_RecordsTheNearestNeighborsInOrder_UpToTheRecordedLength_EvenWhenTheJudgeLooksAtFewer()
    {
        var answer = MemoryJudge.Decide(V(1, 0, 0), MixedMemory(), neighbors: 3, maxDistance: 0.5);
        var a = answer.ToAnnotations();

        var classes = a[MemoryAnswer.NeighborClassesKey].Split(',');
        var distances = a[MemoryAnswer.NeighborDistancesKey].Split(',').Select(double.Parse).ToArray();
        Assert.Equal(MemoryJudge.RecordedNeighbors, classes.Length);
        Assert.Equal(classes.Length, distances.Length);
        Assert.Equal(distances.Order().ToArray(), distances);   // 가까운 쪽부터
        Assert.Equal("3", a["memory:neighbors"]);               // 판정에 쓴 k 는 그대로
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    public void Reconsider_FromTheLedgerRecord_GivesTheSameAnswerAsJudgingAgainWithThatK(int k)
    {
        var memory = MixedMemory();
        var query = V(1, 0.01f, 0);
        // 장부에 남은 것은 k = 5(기본)로 판정한 줄이다.
        var recorded = MemoryJudge.Decide(query, memory, neighbors: 5, maxDistance: 0.3).ToAnnotations();

        var expected = MemoryJudge.Decide(query, memory, neighbors: k, maxDistance: 0.3);
        var again = MemoryJudge.Reconsider(recorded, k, maxDistance: 0.3);

        Assert.NotNull(again);
        Assert.Equal(expected.Class, again!.Class);
        Assert.Equal(expected.AgreedClass, again.AgreedClass);
        Assert.Equal(expected.Abstained, again.Abstained);
        Assert.Equal(expected.NearestDistance!.Value, again.NearestDistance!.Value, precision: 6); // 기록은 소수 여섯째 자리까지
    }

    [Fact]
    public void Reconsider_GivesNothing_WhenThereIsNoRecord_OrTheKIsLongerThanTheRecord()
    {
        Assert.Null(MemoryJudge.Reconsider(new Dictionary<string, string> { ["memory:neighbors"] = "5" }, 3, 0.15)); // 옛 장부 줄
        var recorded = MemoryJudge.Decide(V(1, 0, 0), MixedMemory(), neighbors: 5, maxDistance: 0.3).ToAnnotations();
        Assert.Null(MemoryJudge.Reconsider(recorded, MemoryJudge.RecordedNeighbors + 1, 0.3));
        Assert.Null(MemoryJudge.Reconsider(recorded, 0, 0.3));
    }

    [Fact]
    public void Decide_WithAKLongerThanTheRecordedLength_RecordsAtLeastThatManyNeighbors()
    {
        var memory = Enumerable.Range(0, 14).Select(i => new MemoryCase($"c{i}", "OK", V(1f, 0.01f * i, 0f))).ToList();
        var a = MemoryJudge.Decide(V(1, 0, 0), memory, neighbors: 12, maxDistance: 0.5).ToAnnotations();
        Assert.Equal(12, a[MemoryAnswer.NeighborClassesKey].Split(',').Length);
    }

    [Fact]
    public void AnAbstainBecauseTheMemoryIsTooSmall_RecordsNoNeighbors_AsBefore()
    {
        var a = MemoryJudge.Decide(V(1, 0, 0), [new MemoryCase("c", "OK", V(1, 0, 0))], neighbors: 5, maxDistance: 0.15).ToAnnotations();
        Assert.False(a.ContainsKey(MemoryAnswer.NeighborClassesKey));
    }
}
