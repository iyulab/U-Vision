extern alias bench;

using bench::UVision.Benchmark;
using UVision.Api.Services.Memory;
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
        Assert.Equal(0.2, PatchDistance.PatchScore(query, 2, bank, 2, 2), 5);
    }

    [Fact]
    public void GlobalScore_is_nearest_cosine_distance_to_the_bank()
    {
        float[] query = [0.6f, 0.8f];
        IReadOnlyList<float[]> bank = [[1f, 0f], [0f, 1f]];
        Assert.Equal(0.2, PatchDistance.GlobalScore(query, bank), 5);
    }

    [Fact]
    public void PatchMap_TiledSearch_IsIdenticalToTheDirectLoop_ForSizesThatDoNotFillATile()
    {
        // 질의 · 기억 개수와 차원이 묶음 크기의 배수가 아니다 — 가장자리 묶음까지 같은 값이어야 한다.
        const int dim = 37, queryPatches = 21, bankPatches = 4001; // 기억 조각(약 1770 패치)이 셋: 꽉 찬 둘 + 모자란 하나
        var random = new Random(7);
        float[] Unit(int count)
        {
            var values = new float[count * dim];
            for (var i = 0; i < count; i++)
            {
                double norm = 0;
                for (var j = 0; j < dim; j++) { values[i * dim + j] = (float)(random.NextDouble() - 0.5); norm += values[i * dim + j] * values[i * dim + j]; }
                for (var j = 0; j < dim; j++) values[i * dim + j] /= (float)Math.Sqrt(norm);
            }
            return values;
        }
        var query = Unit(queryPatches);
        var bank = Unit(bankPatches);

        var expected = new double[queryPatches];
        for (var q = 0; q < queryPatches; q++)
        {
            var best = double.NegativeInfinity;
            for (var m = 0; m < bankPatches; m++)
                best = Math.Max(best, PatchDistance.Dot(query, q * dim, bank, m * dim, dim));
            expected[q] = 1 - best;
        }

        Assert.Equal(expected, PatchDistance.PatchMap(query, queryPatches, bank, bankPatches, dim));
    }

    [Fact]
    public void SubsampleBank_KeepsTheRequestedShare_OfRealBankPatches_Reproducibly()
    {
        const int dim = 3, patches = 40;
        var bank = new float[patches * dim];
        for (var i = 0; i < patches; i++)
            for (var j = 0; j < dim; j++) bank[i * dim + j] = i * 10 + j; // 패치 i 의 값에서 어느 패치인지 읽힌다

        var a = PatchBench.SubsampleBank(bank, patches, dim, 0.25);
        var b = PatchBench.SubsampleBank(bank, patches, dim, 0.25);

        Assert.Equal(10 * dim, a.Length);
        Assert.Equal(a, b); // 시드 고정 — 같은 입력 · 비율이면 같은 부분표본
        var picked = Enumerable.Range(0, 10).Select(k => (int)(a[k * dim] / 10)).ToList();
        Assert.Equal(picked.Order().ToList(), picked); // 원래 순서를 지킨다
        Assert.Equal(10, picked.Distinct().Count()); // 중복 없음
        for (var k = 0; k < 10; k++) // 패치 하나가 통째로 옮겨진다(차원이 섞이지 않는다)
            Assert.Equal(new[] { picked[k] * 10f, picked[k] * 10f + 1, picked[k] * 10f + 2 }, a[(k * dim)..((k + 1) * dim)]);
    }

    [Fact]
    public void ParseFractions_KeepsOnlySharesBetweenZeroAndOne()
    {
        Assert.Equal([0.5, 0.1], PatchBench.ParseFractions("0.5, 0.1,1,0,1.5"));
        Assert.Empty(PatchBench.ParseFractions(null));
    }

    [Fact]
    public void Dot_handles_lengths_that_are_not_a_vector_multiple()
    {
        float[] a = [.. Enumerable.Repeat(1f, 19)];
        Assert.Equal(19f, PatchDistance.Dot(a, 0, a, 0, 19));
    }

    [Fact]
    public void PatchMap_IsTheNearestDistancePerQueryPatch_AndTheScoreIsItsMaximum()
    {
        float[] bank = [1f, 0f, 0f, 1f];
        float[] query = [1f, 0f, 0.6f, 0.8f];

        var map = PatchDistance.PatchMap(query, 2, bank, 2, 2);

        Assert.Equal(2, map.Length);
        Assert.Equal(0.0, map[0], 5);
        Assert.Equal(0.2, map[1], 5);
        Assert.Equal(map.Max(), PatchDistance.PatchScore(query, 2, bank, 2, 2), 10);
    }
}
