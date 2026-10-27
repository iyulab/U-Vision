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
    public static double PatchScore(float[] query, int queryPatches, float[] bank, int bankPatches, int dim) =>
        PatchMap(query, queryPatches, bank, bankPatches, dim).Max();

    /// <summary>
    /// 질의 패치마다 기억 패치 전부까지의 최근접 코사인 거리(행 우선) — 패치 점수의 재료이자 어디가 정상 기억에서 먼지를 보여 주는
    /// 열지도의 값이다.
    /// </summary>
    public static double[] PatchMap(float[] query, int queryPatches, float[] bank, int bankPatches, int dim)
    {
        if (queryPatches < 1 || bankPatches < 1)
            throw new ArgumentException("질의 · 기억 패치가 각각 1개 이상이어야 한다");
        // 기억이 크면 한 질의 패치가 기억 전체를 훑을 때마다 기억이 메모리에서 다시 읽힌다(질의 256 × 기억 수십~수백 MB). 질의 패치를
        // 몇 개씩 묶어 그 묶음이 같은 기억 조각을 캐시에서 함께 쓰게 한다. 각 쌍의 내적과 최댓값은 그대로라 결과는 바이트까지 같다.
        var best = new float[queryPatches];
        Array.Fill(best, float.NegativeInfinity);
        var bankTile = Math.Max(1, BankTileBytes / (dim * sizeof(float)));
        var tiles = (queryPatches + QueryTile - 1) / QueryTile;
        Parallel.For(0, tiles, t =>
        {
            var q0 = t * QueryTile;
            var q1 = Math.Min(queryPatches, q0 + QueryTile);
            for (var m0 = 0; m0 < bankPatches; m0 += bankTile)
            {
                var m1 = Math.Min(bankPatches, m0 + bankTile);
                for (var q = q0; q < q1; q++)
                {
                    var top = best[q];
                    for (var m = m0; m < m1; m++)
                        top = Math.Max(top, Dot(query, q * dim, bank, m * dim, dim));
                    best[q] = top;
                }
            }
        });
        var perPatch = new double[queryPatches];
        for (var q = 0; q < queryPatches; q++)
            perPatch[q] = 1 - (double)best[q];
        return perPatch;
    }

    // 묶음 크기 — 질의 8개 · 기억 조각 256 KB(코어 캐시에 들어가는 크기). dinov2-small(384차원)에서 기억 600장일 때 이전 구현의 약 절반
    // 시간(개발 PC, 코어 30). 값을 키워도 빨라지지 않았다.
    private const int QueryTile = 8;
    private const int BankTileBytes = 256 * 1024;

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
