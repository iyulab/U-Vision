using System.Globalization;

namespace UVision.Api.Services.Memory;

/// <summary>기억에 든 사례 하나 — 사람이 확정한 클래스와 그 이미지의 벡터.</summary>
public sealed record MemoryCase(string ImageId, string Class, float[] Vector);

/// <summary>
/// 기억 단의 답 — 답했으면 <see cref="Class"/>, 기권이면 <see cref="Abstained"/> 사유. 어느 쪽이든 최근접 거리와 본 이웃
/// 수를, 이웃이 한 클래스로 모였으면 그 클래스(<see cref="AgreedClass"/> — 문턱 밖이라 기권했어도)를 남긴다. 장부에 적어
/// 문턱을 재생으로 고르기 위해서다: 더 넓은 문턱이었다면 무엇을 답했을지가 거기서 나온다.
/// </summary>
public sealed record MemoryAnswer(
    string? Class, double Confidence, string? Abstained, double? NearestDistance, int Neighbors, string? AgreedClass = null,
    IReadOnlyList<RankedNeighbor>? Ranked = null)
{
    /// <summary>장부 annotation — 문턱 재생의 원천 자료.</summary>
    public IReadOnlyDictionary<string, string> ToAnnotations()
    {
        var a = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory:neighbors"] = Neighbors.ToString(CultureInfo.InvariantCulture),
        };
        if (NearestDistance is { } d)
            a["memory:nearest_distance"] = d.ToString("0.######", CultureInfo.InvariantCulture);
        if (AgreedClass is not null)
            a["memory:agreed_class"] = AgreedClass;
        // 가까운 이웃의 순서(가까운 쪽부터) — 다른 k 의 답을 장부에서 다시 셀 수 있게 한다(재생 · 옵션 추천의 원천 자료).
        if (Ranked is { Count: > 0 })
        {
            a[NeighborClassesKey] = string.Join(',', Ranked.Select(r => r.Class));
            a[NeighborDistancesKey] = string.Join(',', Ranked.Select(r => r.Distance.ToString("0.######", CultureInfo.InvariantCulture)));
        }
        return a;
    }

    public const string NeighborClassesKey = "memory:neighbor_classes";
    public const string NeighborDistancesKey = "memory:neighbor_distances";
}

/// <summary>기억에서 찾은 이웃 한 건 — 클래스와 질의까지의 코사인 거리.</summary>
public sealed record RankedNeighbor(string Class, double Distance);

/// <summary>
/// 확정 사례 k-최근접 판정 — 순수 함수. 답하거나 기권만 한다(위로 올리기 · 확정 여부는 라우터 · 정책의 일).
/// <para>
/// 답하는 조건: 기억에 이웃이 k 개 이상 있고, 최근접 k 개가 모두 같은 클래스이고, 가장 가까운 거리가 문턱 안.
/// 신뢰도 = 1 − 최근접 거리. 기억이 모자람 · 이웃이 갈림 · 문턱 밖이면 기권한다. 벡터는 L2 정규화돼 있다고 본다.
/// 정지 촬영 규모(확정 사례 수천)라 전수 비교한다 — 사례가 그 규모를 넘으면 근사 최근접 색인이 필요하다.
/// </para>
/// </summary>
public static class MemoryJudge
{
    /// <summary>장부에 남기는 이웃 순서의 최대 길이 — 이보다 큰 k 의 답은 다시 셀 수 없다(기본 k 는 5).</summary>
    public const int RecordedNeighbors = 9;

    public static MemoryAnswer Decide(
        float[] query, IReadOnlyCollection<MemoryCase> memory, int neighbors, double maxDistance)
    {
        if (memory.Count < neighbors)
            return new(null, 0, $"memory has {memory.Count} < {neighbors} cases", null, memory.Count);

        // 판정은 가까운 k 개만 보지만, 순서는 더 길게(RecordedNeighbors) 들고 있다가 장부에 남긴다.
        var ranked = memory
            .Select(c => new RankedNeighbor(c.Class, VectorMath.CosineDistance(query, c.Vector)))
            .OrderBy(n => n.Distance)
            .Take(Math.Max(neighbors, RecordedNeighbors))
            .ToList();
        var nearest = ranked.Take(neighbors).ToList();
        var closest = nearest[0].Distance;

        if (nearest.Select(n => n.Class).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            return new(null, 0, "neighbors disagree", closest, neighbors, Ranked: ranked);
        var agreed = nearest[0].Class;
        if (closest > maxDistance)
            return new(null, 0, "nearest case beyond threshold", closest, neighbors, agreed, ranked);
        return new(agreed, Math.Clamp(1.0 - closest, 0.0, 1.0), null, closest, neighbors, agreed, ranked);
    }

    /// <summary>
    /// 장부에 남은 이웃 순서로 <paramref name="neighbors"/> 개를 다시 본 답 — 같은 질의 · 같은 기억이면 <see cref="Decide"/> 와 같다.
    /// 기록이 없거나 k 가 기록보다 길면 null(다시 셀 수 없다).
    /// </summary>
    public static MemoryAnswer? Reconsider(IReadOnlyDictionary<string, string> annotations, int neighbors, double maxDistance)
    {
        if (!annotations.TryGetValue(MemoryAnswer.NeighborClassesKey, out var classesRaw)
            || !annotations.TryGetValue(MemoryAnswer.NeighborDistancesKey, out var distancesRaw))
            return null;
        var classes = classesRaw.Split(',');
        var distances = distancesRaw.Split(',')
            .Select(d => double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        if (classes.Length != distances.Length || neighbors < 1 || neighbors > classes.Length || distances.Any(double.IsNaN)) return null;

        var nearest = classes.Zip(distances, (c, d) => new RankedNeighbor(c, d)).Take(neighbors).ToList();
        var closest = nearest[0].Distance;
        if (nearest.Select(n => n.Class).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            return new(null, 0, "neighbors disagree", closest, neighbors);
        var agreed = nearest[0].Class;
        if (closest > maxDistance)
            return new(null, 0, "nearest case beyond threshold", closest, neighbors, agreed);
        return new(agreed, Math.Clamp(1.0 - closest, 0.0, 1.0), null, closest, neighbors, agreed);
    }
}
