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
}
