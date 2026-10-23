namespace UVision.Api.Services.Memory;

/// <summary>기억 단의 벡터 계산 — 순수 함수.</summary>
public static class VectorMath
{
    /// <summary>L2 정규화한 새 배열. 영벡터는 그대로(거리 비교에서 어떤 이웃과도 가깝지 않다).</summary>
    public static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return norm == 0 ? [.. v] : [.. v.Select(x => (float)(x / norm))];
    }

    /// <summary>정규화된 두 벡터의 코사인 거리(1 − 내적, 0~2). 차원이 다르면 <see cref="ArgumentException"/>.</summary>
    public static double CosineDistance(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"벡터 차원이 다르다: {a.Length} ≠ {b.Length}");
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += (double)a[i] * b[i];
        return 1.0 - dot;
    }
}
