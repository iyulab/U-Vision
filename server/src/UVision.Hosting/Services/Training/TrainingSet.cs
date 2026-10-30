using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UVision.Api.Models;

namespace UVision.Api.Services.Training;

/// <summary>
/// 학습 후보 한 건 — 사람이 라벨한 검사 기록. 정답은 사람 확정(operative) 라벨뿐이다: 오라클은 검토 순서 · 2차 의견,
/// VLM 판정은 정답이 아니다(자기채점 순환).
/// </summary>
/// <param name="Label">사람 확정 라벨 — 시나리오의 클래스 id(이분 판정 · 오투입 확인은 OK/NG).</param>
/// <param name="LabeledAt">라벨을 마지막으로 쓴 시각(ISO 8601) — 재빌드 제안이 «모델 이후 새 라벨»을 센다.</param>
/// <param name="Conflicted">블라인드 감사에서 직전 라벨과 어긋나 아직 풀리지 않았다 — 정답으로 쓰지 않는다.</param>
/// <param name="HardNegative">판정원이 엇갈렸거나 검토가 필요했던 건(<c>requires_review</c> · 불일치) — 가장 값진 표본이라 늘 넣는다.</param>
/// <param name="Stream">라벨의 출제 흐름 표식(<see cref="Services.Label.LabelStream"/>) — 없으면 표식 없는 라벨. 선정에는 쓰지 않고 export 가 그대로 싣는다.</param>
public sealed record TrainingCandidate(
    string ImageId, string Date, string Label, string LabeledAt, bool Conflicted, bool HardNegative, string? Stream = null);

/// <summary>선정 결과 — 학습에 넣는 건과 뺀 이유별 수.</summary>
public sealed record TrainingSelection(
    IReadOnlyList<TrainingCandidate> Selected, int DroppedConflicted, int DroppedOkOverCap);

/// <summary>
/// 학습 데이터 선정 규칙 — 순수 함수. 열린 감사 충돌이 있는 라벨은 빼고, NG 쪽(불량 유형 전부)과 검토 건은
/// 전량 넣고, 나머지 OK 는 NG 쪽 건수 × <see cref="ScenarioTraining.OkRatioCap"/> 까지만 넣는다(희소한 NG 가 묻히지 않게).
/// OK 표본은 image_id 해시 순이라 결정론적이고 날짜에 치우치지 않는다.
/// </summary>
public static class TrainingSetSelector
{
    public static TrainingSelection Select(IEnumerable<TrainingCandidate> candidates, ScenarioTraining rules)
    {
        var usable = candidates.ToList();
        var conflicted = usable.RemoveAll(c => c.Conflicted);

        var cappedOk = usable.Where(c => IsOk(c.Label) && !c.HardNegative).ToList();
        var always = usable.Except(cappedOk).ToList();
        var ngSide = usable.Count(c => !IsOk(c.Label));

        // NG 가 하나도 없으면 상한을 정할 기준이 없다 — OK 만으로는 어차피 학습할 수 없으니(내보내기 경고) 그대로 둔다.
        var cap = ngSide == 0 ? cappedOk.Count : (int)Math.Ceiling(ngSide * rules.OkRatioCap);
        var keptOk = cappedOk.OrderBy(c => Rank(c.ImageId), StringComparer.Ordinal).Take(cap).ToList();

        var selected = always.Concat(keptOk)
            .OrderBy(c => c.Date, StringComparer.Ordinal)
            .ThenBy(c => c.ImageId, StringComparer.Ordinal)
            .ToList();
        return new TrainingSelection(selected, conflicted, cappedOk.Count - keptOk.Count);
    }

    /// <summary>좋은 쪽 라벨인가 — 검사 · 오투입 확인 모두 사람 라벨의 좋은 쪽은 <c>OK</c> 다.</summary>
    public static bool IsOk(string label) => string.Equals(label, ScenarioVocabulary.Ok, StringComparison.OrdinalIgnoreCase);

    private static string Rank(string imageId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageId)));
}

/// <summary>클래스 하나의 학습 표본 수.</summary>
public sealed record TrainingClassCount(string Id, int Count);

/// <summary>
/// 학습 준비 신호 — 빌드 · 재빌드는 관리자가 하고, 이 신호는 «할 때가 됐다»만 알린다. 판단할 근거가
/// 없으면 null 이다(신호를 false 로 위장하지 않는다).
/// </summary>
/// <param name="Classes">시나리오 클래스마다 선정된 표본 수.</param>
/// <param name="MinPerClass">클래스당 기준.</param>
/// <param name="BuildReady">쓰는 모델이 없을 때 — 모든 클래스가 기준에 닿았나. 모델이 있으면 null.</param>
/// <param name="ActiveVersion">쓰는 모델 버전(없으면 null).</param>
/// <param name="NewLabelsSinceModel">쓰는 모델의 데이터셋 이후 새로 확정된 라벨 수 — 모델의 학습 데이터를 모르면 null.</param>
/// <param name="RebuildSuggested">새 라벨이 재빌드 기준에 닿았나 — 모델이 없거나 학습 데이터를 모르면 null.</param>
public sealed record TrainingReadinessResult(
    IReadOnlyList<TrainingClassCount> Classes,
    int MinPerClass,
    bool? BuildReady,
    string? ActiveVersion,
    int? NewLabelsSinceModel,
    bool? RebuildSuggested);

public static class TrainingReadiness
{
    /// <param name="classIds">시나리오가 받는 라벨(클래스 id) — 표본이 없는 클래스도 0 으로 보인다.</param>
    /// <param name="selected">선정된 표본(<see cref="TrainingSetSelector"/>).</param>
    /// <param name="activeVersion">쓰는 모델 버전 — 없으면 콜드스타트.</param>
    /// <param name="activeDatasetCreatedAt">쓰는 모델이 배운 데이터셋의 생성 시각 — 모르면 null.</param>
    public static TrainingReadinessResult Evaluate(
        IReadOnlyList<string> classIds, IReadOnlyList<TrainingCandidate> selected, ScenarioTraining rules,
        string? activeVersion, string? activeDatasetCreatedAt)
    {
        var counts = classIds
            .Select(id => new TrainingClassCount(
                id, selected.Count(c => string.Equals(c.Label, id, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        if (activeVersion is null)
            return new(counts, rules.MinPerClass, counts.All(c => c.Count >= rules.MinPerClass), null, null, null);

        if (activeDatasetCreatedAt is null || !TryParse(activeDatasetCreatedAt, out var since))
            return new(counts, rules.MinPerClass, null, activeVersion, null, null);

        var fresh = selected.Count(c => TryParse(c.LabeledAt, out var at) && at > since);
        return new(counts, rules.MinPerClass, null, activeVersion, fresh, fresh >= rules.RebuildMinNewLabels);
    }

    private static bool TryParse(string iso, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
}
