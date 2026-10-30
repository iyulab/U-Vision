namespace UVision.Api.Services.Memory;

/// <summary>
/// 기억 패치를 줄일 때 «무엇을 남기는가» — 무작위가 아니라 탐욕 k-center(PatchCore 의 코어셋 선택): 이미 남긴 패치에서 가장 먼
/// 패치를 차례로 더한다. 점수는 «기억에서 가장 가까운 패치까지의 거리» 라서, 은행이 작아져도 정상의 가장자리(드문 정상 질감)를
/// 덮고 있어야 정상이 불량으로 보이지 않는다. 무작위는 흔한 패치만 남기고 드문 정상을 잃는다.
/// 거리는 128 차원 무작위 투영 위에서 잰다(원 차원에서 재면 선택이 점수 계산만큼 비싸다). 시드가 같으면 결과가 같다.
/// 판정에는 쓰이지 않는다 — 점수를 바꾸는 쪽이라 같은 표로 재생한 뒤에만 은행에 결선한다.
/// </summary>
public static class CoresetSelector
{
    /// <summary>선택 거리를 재는 투영 차원(이보다 작은 벡터는 투영하지 않는다).</summary>
    public const int ProjectedDim = 128;

    private const int ChunkSize = 4096;

    /// <summary>
    /// <paramref name="vectors"/>(행 우선, <paramref name="dim"/> 개씩 <paramref name="count"/> 개) 가운데 <paramref name="keep"/> 개의
    /// 인덱스를 오름차순으로 돌려준다. <paramref name="keep"/> 이 전체 이상이면 전부.
    /// </summary>
    public static int[] Select(float[] vectors, int count, int dim, int keep, int seed = 42)
    {
        if (count < 1 || dim < 1 || keep < 1)
            throw new ArgumentException("벡터 · 차원 · 남길 수가 각각 1 이상이어야 한다");
        if (vectors.Length < (long)count * dim)
            throw new ArgumentException("벡터 배열이 count × dim 보다 짧다");
        if (keep >= count) return [.. Enumerable.Range(0, count)];

        var (space, width) = Project(vectors, count, dim, seed);
        var nearest = new float[count]; // 지금까지 남긴 패치까지의 최근접 제곱 거리, 남긴 패치는 -1
        Array.Fill(nearest, float.PositiveInfinity);

        var chunks = (count + ChunkSize - 1) / ChunkSize;
        var chosen = new int[keep];
        var best = new (float Value, int Index)[chunks];
        var next = new Random(seed).Next(count);
        for (var step = 0; ; step++)
        {
            chosen[step] = next;
            nearest[next] = -1;
            if (step == keep - 1) break;

            var center = next * width;
            Parallel.For(0, chunks, c =>
            {
                var from = c * ChunkSize;
                var to = Math.Min(count, from + ChunkSize);
                var top = (Value: -1f, Index: -1);
                for (var i = from; i < to; i++)
                {
                    if (nearest[i] < 0) continue; // 이미 남김
                    var distance = SquaredDistance(space, i * width, space, center, width);
                    if (distance < nearest[i]) nearest[i] = distance;
                    if (nearest[i] > top.Value) top = (nearest[i], i);
                }
                best[c] = top;
            });

            var farthest = (Value: -1f, Index: -1);
            foreach (var candidate in best)
                if (candidate.Value > farthest.Value) // 같으면 앞 조각(작은 인덱스)이 이긴다
                    farthest = candidate;
            next = farthest.Index;
        }

        Array.Sort(chosen);
        return chosen;
    }

    /// <summary>선택한 인덱스의 벡터만 모은 배열.</summary>
    public static float[] Gather(float[] vectors, int dim, int[] indices)
    {
        var picked = new float[indices.Length * dim];
        for (var k = 0; k < indices.Length; k++)
            Array.Copy(vectors, (long)indices[k] * dim, picked, (long)k * dim, dim);
        return picked;
    }

    private static (float[] Space, int Width) Project(float[] vectors, int count, int dim, int seed)
    {
        if (dim <= ProjectedDim) return (vectors, dim);

        // 가우스 무작위 행렬(전치로 두어 한 출력 좌표가 원 벡터와의 내적이 되게) — 거리를 대체로 보존한다(Johnson–Lindenstrauss).
        var random = new Random(seed ^ 0x5bd1e995);
        var matrix = new float[ProjectedDim * dim];
        var scale = 1f / MathF.Sqrt(ProjectedDim);
        for (var i = 0; i < matrix.Length; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            matrix[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2)) * scale;
        }

        var projected = new float[(long)count * ProjectedDim];
        Parallel.For(0, count, i =>
        {
            for (var j = 0; j < ProjectedDim; j++)
                projected[(long)i * ProjectedDim + j] = PatchDistance.Dot(vectors, i * dim, matrix, j * dim, dim);
        });
        return (projected, ProjectedDim);
    }

    private static float SquaredDistance(float[] a, int aOffset, float[] b, int bOffset, int length)
    {
        var width = System.Numerics.Vector<float>.Count;
        var sum = System.Numerics.Vector<float>.Zero;
        var i = 0;
        for (; i <= length - width; i += width)
        {
            var diff = new System.Numerics.Vector<float>(a, aOffset + i) - new System.Numerics.Vector<float>(b, bOffset + i);
            sum += diff * diff;
        }
        var total = System.Numerics.Vector.Sum(sum);
        for (; i < length; i++)
        {
            var diff = a[aOffset + i] - b[bOffset + i];
            total += diff * diff;
        }
        return total;
    }
}
