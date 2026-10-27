extern alias bench;

using bench::UVision.Benchmark;
using Xunit;

namespace UVision.Tests;

/// <summary>패치 이상 점수 재생의 순수 계산 — 문턱에 기대지 않는 비교 지표와 최근접 패치 점수.</summary>
public class PatchScoringTests
{
    [Fact]
    public void Auroc_separated_scores_are_one_and_ties_are_half()
    {
        Assert.Equal(1.0, PatchScoring.Auroc([0.1, 0.2], [0.5, 0.6]));
        Assert.Equal(0.0, PatchScoring.Auroc([0.5, 0.6], [0.1, 0.2]));
        Assert.Equal(0.5, PatchScoring.Auroc([0.3], [0.3]));
    }

    [Fact]
    public void MissedAtFalseAlarm_counts_defects_at_or_below_the_threshold_that_allows_the_rate()
    {
        double[] normal = [.. Enumerable.Range(1, 100).Select(i => i / 100.0)]; // 0.01 … 1.00
        // 오경보 5 % 허용 → 정상 5 개(0.96~1.00)가 문턱 위, 문턱 = 0.95.
        Assert.Equal(2, PatchScoring.MissedAtFalseAlarm(normal, [0.50, 0.95, 0.96, 2.0], 0.05));
        // 오경보 0 → 문턱 = 정상 최댓값.
        Assert.Equal(2, PatchScoring.MissedAtFalseAlarm(normal, [0.50, 1.00, 1.01, 2.0], 0.0));
    }

    [Fact]
    public void PatchScore_is_the_worst_patch_nearest_distance()
    {
        // 차원 2, 기억 패치 두 개: (1,0) · (0,1). 질의 패치 (1,0) 은 거리 0, (0.6,0.8) 은 최근접 (0,1) 까지 1-0.8.
        float[] bank = [1f, 0f, 0f, 1f];
        float[] query = [1f, 0f, 0.6f, 0.8f];
        Assert.Equal(0.2, PatchScoring.PatchScore(query, 2, bank, 2, 2), 5);
    }

    [Fact]
    public void GlobalScore_is_nearest_cosine_distance_to_the_bank()
    {
        float[] query = [0.6f, 0.8f];
        IReadOnlyList<float[]> bank = [[1f, 0f], [0f, 1f]];
        Assert.Equal(0.2, PatchScoring.GlobalScore(query, bank), 5);
    }

    [Fact]
    public void Dot_handles_lengths_that_are_not_a_vector_multiple()
    {
        float[] a = [.. Enumerable.Repeat(1f, 19)];
        Assert.Equal(19f, PatchScoring.Dot(a, 0, a, 0, 19));
    }
}
