using System.Numerics;

namespace UVision.Api.Services.Memory;

/// <summary>
/// 패치 이상 점수의 순수 계산 — 작은 결함은 이미지 하나의 전역 벡터로는 정상과 가려지지 않아, 패치 단위로 기억과 견준다.
/// 벡터는 L2 정규화돼 있다고 본다(코사인 = 내적). 판정에는 쓰이지 않고 장부와
/// 재생 도구가 같은 계산을 쓴다 — 점수의 정의가 두 곳에 있으면 재생이 장부와 다른 것을 잰다.
/// </summary>
public static class PatchDistance
{
    /// <summary>전역 점수: 기억 사진 전역 벡터까지 최근접 코사인 거리.</summary>
    public static double GlobalScore(float[] query, IReadOnlyList<float[]> bank)
    {
        var best = double.NegativeInfinity;
        foreach (var v in bank)
            best = Math.Max(best, Dot(query, 0, v, 0, query.Length));
        return 1 - best;
    }

    /// <summary>
    /// 패치 점수: 질의 패치마다 기억 패치 전부까지 최근접 코사인 거리, 이미지 점수 = 그 최댓값(PatchCore 관행).
    /// 패치는 한 배열에 행 우선으로 이어 붙는다(<paramref name="dim"/> 개씩).
    /// </summary>
    public static double PatchScore(float[] query, int queryPatches, float[] bank, int bankPatches, int dim)
    {
        if (queryPatches < 1 || bankPatches < 1)
            throw new ArgumentException("질의 · 기억 패치가 각각 1개 이상이어야 한다");
        var perPatch = new double[queryPatches];
        Parallel.For(0, queryPatches, q =>
        {
            var best = double.NegativeInfinity;
            for (var m = 0; m < bankPatches; m++)
                best = Math.Max(best, Dot(query, q * dim, bank, m * dim, dim));
            perPatch[q] = 1 - best;
        });
        return perPatch.Max();
    }

    public static float Dot(float[] a, int aOffset, float[] b, int bOffset, int length)
    {
        var width = Vector<float>.Count;
        var sum = Vector<float>.Zero;
        var i = 0;
        for (; i <= length - width; i += width)
            sum += new Vector<float>(a, aOffset + i) * new Vector<float>(b, bOffset + i);
        var total = Vector.Sum(sum);
        for (; i < length; i++)
            total += a[aOffset + i] * b[bOffset + i];
        return total;
    }
}
