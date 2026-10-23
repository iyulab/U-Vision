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
    string? Class, double Confidence, string? Abstained, double? NearestDistance, int Neighbors, string? AgreedClass = null)
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
        return a;
    }
}

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
    public static MemoryAnswer Decide(
        float[] query, IReadOnlyCollection<MemoryCase> memory, int neighbors, double maxDistance)
    {
        if (memory.Count < neighbors)
            return new(null, 0, $"memory has {memory.Count} < {neighbors} cases", null, memory.Count);

        var nearest = memory
            .Select(c => (c.Class, Distance: VectorMath.CosineDistance(query, c.Vector)))
            .OrderBy(n => n.Distance)
            .Take(neighbors)
            .ToList();
        var closest = nearest[0].Distance;

        if (nearest.Select(n => n.Class).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            return new(null, 0, "neighbors disagree", closest, neighbors);
        var agreed = nearest[0].Class;
        if (closest > maxDistance)
            return new(null, 0, "nearest case beyond threshold", closest, neighbors, agreed);
        return new(agreed, Math.Clamp(1.0 - closest, 0.0, 1.0), null, closest, neighbors, agreed);
    }
}
