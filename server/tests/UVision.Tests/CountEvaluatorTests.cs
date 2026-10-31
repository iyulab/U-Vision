using UVision.Api.Services.Counting;
using Xunit;

namespace UVision.Tests;

/// <summary>수 세기 · 허용 범위 대조 — 순수 함수의 경계(문턱 · 종류 · 허용 오차 · 흔들림).</summary>
public class CountEvaluatorTests
{
    private static Detection D(string cls, double confidence) => new(cls, 0.1, 0.1, 0.1, 0.1, confidence);

    [Fact]
    public void CountsOnlyBoxesAtOrAboveTheThreshold_ByClass()
    {
        var detections = new[] { D("pill", 0.9), D("pill", 0.5), D("pill", 0.49), D("cap", 0.8) };

        var r = CountEvaluator.Count(detections, 0.5, null, expected: null, tolerance: null);

        Assert.Equal(3, r.Total);
        Assert.Equal(2, r.ByClass["pill"]);
        Assert.Equal(1, r.ByClass["cap"]);
        Assert.Equal(3, r.Boxes.Count);
        Assert.Null(r.WithinRange); // 기대가 없으면 세기만
        Assert.Null(r.Expected);
    }

    [Fact]
    public void OnlyTheNamedClassesAreCounted()
    {
        var detections = new[] { D("pill", 0.9), D("cap", 0.9), D("cap", 0.9) };

        var r = CountEvaluator.Count(detections, 0.5, ["cap"], expected: 2, tolerance: 0);

        Assert.Equal(2, r.Total);
        Assert.Equal(new[] { "cap" }, r.ByClass.Keys);
        Assert.True(r.WithinRange);
    }

    [Theory]
    [InlineData(10, 10, 0, true)]
    [InlineData(9, 10, 0, false)]
    [InlineData(11, 10, 0, false)]
    [InlineData(9, 10, 1, true)]
    [InlineData(12, 10, 1, false)]
    [InlineData(9, 10, -3, false)] // 음수 허용 오차는 0 으로 본다
    public void WithinRange_IsExpectedPlusOrMinusTolerance(int count, int expected, int tolerance, bool within)
    {
        var detections = Enumerable.Range(0, count).Select(_ => D("pill", 0.95)).ToList();

        var r = CountEvaluator.Count(detections, 0.5, null, expected, tolerance);

        Assert.Equal(within, r.WithinRange);
        Assert.False(r.Uncertain); // 문턱에서 먼 상자뿐이면 흔들리지 않는다
        Assert.Equal(Math.Max(0, tolerance), r.Tolerance);
    }

    [Fact]
    public void ABoxNearTheThreshold_ThatDecidesTheVerdict_MakesItUncertain()
    {
        // 기대 3 · 허용 0. 확실한 상자 2 + 문턱 바로 위(0.52) 한 개 = 3 → 범위 안. 그러나 그 한 개가 없으면 2 → 밖.
        var detections = new[] { D("pill", 0.95), D("pill", 0.9), D("pill", 0.52) };

        var r = CountEvaluator.Count(detections, 0.5, null, expected: 3, tolerance: 0);

        Assert.True(r.WithinRange);
        Assert.True(r.Uncertain);
    }

    [Fact]
    public void ANearThresholdBox_ThatCannotChangeTheVerdict_DoesNotMakeItUncertain()
    {
        // 기대 3 · 허용 1: 2 개든 3 개든 범위 안이다 — 문턱 근처 상자가 결론을 못 바꾼다.
        var detections = new[] { D("pill", 0.95), D("pill", 0.9), D("pill", 0.52) };

        var r = CountEvaluator.Count(detections, 0.5, null, expected: 3, tolerance: 1);

        Assert.True(r.WithinRange);
        Assert.False(r.Uncertain);
    }

    [Fact]
    public void WithoutAnExpectation_UncertainMeansTheCountDependsOnTheNearThresholdBoxes()
    {
        var steady = CountEvaluator.Count([D("pill", 0.95), D("pill", 0.9)], 0.5, null, null, null);
        var shaky = CountEvaluator.Count([D("pill", 0.95), D("pill", 0.45)], 0.5, null, null, null);

        Assert.False(steady.Uncertain);
        Assert.True(shaky.Uncertain); // 0.45 는 문턱 아래지만 띠 안 — 넣으면 2, 빼면 1
        Assert.Equal(1, shaky.Total);
    }

    [Fact]
    public void NoDetections_IsAZeroCount_NotAnError()
    {
        var r = CountEvaluator.Count([], 0.5, null, expected: 0, tolerance: 0);

        Assert.Equal(0, r.Total);
        Assert.True(r.WithinRange);
        Assert.False(r.Uncertain);
        Assert.Empty(r.ByClass);
    }
}
