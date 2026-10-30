extern alias bench;

using bench::UVision.Benchmark;
using Xunit;

namespace UVision.Tests;

/// <summary>순서 있는 등급 판정 측정의 순수 부분 — 답 문장에서 등급 읽기와 정확 · 한 단계 이내 채점.</summary>
public class GradeBenchTests
{
    private static readonly string[] Order = ["상", "중", "하"];

    [Theory]
    [InlineData("상", "상")]
    [InlineData("등급: 중", "중")]
    [InlineData("중에 가깝지만 하는 아니다", "중")] // 가장 먼저 나온 것
    [InlineData("판단 불가", null)]
    public void Parse_reads_the_first_grade_named(string text, string? expected) =>
        Assert.Equal(expected, GradeBench.Parse(text, Order));

    [Fact]
    public void Score_counts_exact_and_within_one_step_and_skips_unjudged()
    {
        (string, string?)[] rows =
        [
            ("상", "상"), // 정확
            ("상", "중"), // 한 단계
            ("상", "하"), // 두 단계 — 틀림
            ("하", "중"), // 한 단계
            ("중", null), // 판정 불가 — 분모에서 뺀다
        ];
        Assert.Equal((1, 3, 4), GradeBench.Score(rows, Order));
    }

    [Fact]
    public void Baselines_show_that_within_one_step_is_uninformative_with_three_grades()
    {
        // 등급 셋·정답 균등: 무작위 정확 1/3, 무작위 ±1 = (2/3 + 3/3 + 2/3) / 3 = 7/9, 가운데 등급만 찍으면 ±1 이 100 %.
        var (randomExact, randomAdjacent, fixedAdjacent, fixedGrade) = GradeBench.Baselines(["상", "중", "하"], Order);
        Assert.Equal(1.0 / 3, randomExact, 6);
        Assert.Equal(7.0 / 9, randomAdjacent, 6);
        Assert.Equal(1.0, fixedAdjacent, 6);
        Assert.Equal("중", fixedGrade);
    }

    [Fact]
    public void Baselines_weigh_by_the_truth_counts_and_ignore_grades_outside_the_order()
    {
        // 상 3 · 하 1 · 순서에 없는 '특' 1(무시): 무작위 ±1 = (3 × 2/3 + 1 × 2/3) / 4 = 2/3, '중' 을 찍으면 넷 모두 한 단계 이내라 100 %.
        var (_, randomAdjacent, fixedAdjacent, fixedGrade) = GradeBench.Baselines(["상", "상", "상", "하", "특"], Order);
        Assert.Equal(2.0 / 3, randomAdjacent, 6);
        Assert.Equal(1.0, fixedAdjacent, 6);
        Assert.Equal("중", fixedGrade);
    }
}
