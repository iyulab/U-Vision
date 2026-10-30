using UVision.Api.Services.Memory;
using Xunit;

namespace UVision.Tests;

/// <summary>기억 패치를 줄일 때 드문 정상을 남기는 탐욕 k-center — 순수 계산.</summary>
public class CoresetSelectorTests
{
    [Fact]
    public void Keeping_everything_returns_every_index()
    {
        float[] v = [1f, 0f, 0f, 1f, 1f, 1f];
        Assert.Equal([0, 1, 2], CoresetSelector.Select(v, 3, 2, 3));
        Assert.Equal([0, 1, 2], CoresetSelector.Select(v, 3, 2, 10));
    }

    [Fact]
    public void Result_is_sorted_distinct_the_requested_size_and_repeatable()
    {
        var random = new Random(7);
        var v = new float[500 * 8];
        for (var i = 0; i < v.Length; i++) v[i] = (float)random.NextDouble();

        var first = CoresetSelector.Select(v, 500, 8, 60);
        Assert.Equal(60, first.Length);
        Assert.Equal(first.Order(), first);
        Assert.Equal(first.Length, first.Distinct().Count());
        Assert.Equal(first, CoresetSelector.Select(v, 500, 8, 60));
        Assert.NotEqual(first, CoresetSelector.Select(v, 500, 8, 60, seed: 5)); // 시드가 시작점을 바꾼다
    }

    [Fact]
    public void Rare_clusters_are_kept_where_a_random_subsample_would_lose_them()
    {
        // 98 개는 한 점 근처(흔한 정상), 하나씩만 있는 먼 점 둘(드문 정상). 셋만 남기면 각 무리에서 하나씩이어야 한다.
        var v = new float[100 * 4];
        var jitter = new Random(1);
        for (var i = 0; i < 98; i++)
        {
            v[i * 4] = 1f + (float)(jitter.NextDouble() * 0.001);
            v[i * 4 + 3] = (float)(jitter.NextDouble() * 0.001);
        }
        v[98 * 4 + 1] = 1f; // 드문 무리 둘
        v[99 * 4 + 2] = 1f;

        var picked = CoresetSelector.Select(v, 100, 4, 3);
        Assert.Contains(98, picked);
        Assert.Contains(99, picked);
        Assert.Single(picked, i => i < 98);
    }

    [Fact]
    public void Duplicate_points_are_not_picked_twice()
    {
        float[] v = [1f, 0f, 1f, 0f, 1f, 0f, 0f, 1f]; // 같은 점 셋 + 다른 점 하나
        var picked = CoresetSelector.Select(v, 4, 2, 2);
        Assert.Equal(2, picked.Length);
        Assert.Contains(3, picked);
    }

    [Fact]
    public void High_dimensional_vectors_are_projected_and_still_cover_a_rare_cluster()
    {
        const int dim = 384;
        var random = new Random(3);
        var v = new float[200 * dim];
        for (var i = 0; i < 199; i++) // 흔한 무리: 첫 좌표 근처
        {
            v[i * dim] = 1f;
            for (var d = 1; d < dim; d++) v[i * dim + d] = (float)(random.NextDouble() * 0.01);
        }
        v[199 * dim + 200] = 1f; // 드문 점 하나(다른 축)

        Assert.Contains(199, CoresetSelector.Select(v, 200, dim, 2));
    }

    [Fact]
    public void Gather_copies_the_chosen_rows()
    {
        float[] v = [1f, 2f, 3f, 4f, 5f, 6f];
        Assert.Equal([5f, 6f, 1f, 2f], CoresetSelector.Gather(v, 2, [2, 0]));
    }
}
