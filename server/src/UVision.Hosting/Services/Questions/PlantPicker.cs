using UVision.Api.Models;

namespace UVision.Api.Services.Questions;

/// <summary>심을 수 있는 기준 이미지 한 장 — 정답은 그 이미지가 OK 인지 어느 불량 유형인지다.</summary>
public sealed record PlantCandidate(string RefId, string Truth);

/// <summary>
/// 정답 심기의 순수 선택 — 심을지(확률)와 어느 기준 이미지를 심을지. 저장소 · HTTP 를 모른다.
/// 기준 이미지는 소수라 풀이자가 외울 수 있으므로, 한 장치에는 모든 후보가 한 번씩 나가기 전에 같은 이미지를 되풀이하지 않는다.
/// </summary>
public static class PlantPicker
{
    /// <summary>이번 문제를 심을 차례인가 — <paramref name="rate"/> 가 0 이하이면 굴리지도 않는다(기본 상태가 심기 없던 때와 같게).</summary>
    public static bool ShouldPlant(double rate, Random rng) => rate > 0 && rng.NextDouble() < rate;

    /// <summary>
    /// 기준 이미지 중 정답을 풀이자가 고를 수 있는 것만 후보로 — 정답 클래스가 이 시나리오의 라벨에 없으면(예: 선언하지 않은 불량 유형명)
    /// 채점할 수 없으므로 심지 않는다. <paramref name="ngLabels"/> 는 NG 기준 이미지의 불량 유형명(<c>refId → 유형</c>).
    /// </summary>
    public static IReadOnlyList<PlantCandidate> Candidates(
        IReadOnlyList<ReferenceInfo> references, IReadOnlyDictionary<string, string> ngLabels, IReadOnlyList<string> labels)
    {
        var allowed = new HashSet<string>(labels, StringComparer.Ordinal);
        var candidates = new List<PlantCandidate>();
        foreach (var r in references)
        {
            var truth = r.Label == ReferenceLabel.Ok
                ? ScenarioVocabulary.Ok
                : ngLabels.TryGetValue(r.RefId, out var type) && !string.IsNullOrWhiteSpace(type) ? type : ScenarioVocabulary.Ng;
            if (allowed.Contains(truth))
                candidates.Add(new PlantCandidate(r.RefId, truth));
        }
        return candidates;
    }

    /// <summary>
    /// 이 장치에 가장 덜 나간 후보 가운데 무작위로 하나. <paramref name="plantedToDevice"/> 는 이 장치에 지금까지 나간 기준 이미지 id
    /// (되풀이 포함). 후보가 없으면 null.
    /// </summary>
    public static PlantCandidate? Pick(
        IReadOnlyList<PlantCandidate> candidates, IReadOnlyList<string> plantedToDevice, Random rng)
    {
        if (candidates.Count == 0)
            return null;
        var counts = plantedToDevice.GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var min = candidates.Min(c => counts.GetValueOrDefault(c.RefId));
        var least = candidates.Where(c => counts.GetValueOrDefault(c.RefId) == min).ToList();
        return least[rng.Next(least.Count)];
    }
}
