using System.Globalization;

namespace UVision.Api.Services.Questions;

/// <summary>
/// 아직 사람이 확정하지 않은 사진 하나 — 문제로 내보낼 수 있는 후보. <see cref="Uncertainty"/> 는 기억 단이 그 사진을 판정할 때
/// 얼마나 헷갈렸나(클수록 불확실)이고, 기억 단이 아무것도 남기지 않았으면 null.
/// </summary>
public sealed record QuestionCandidate(string ImageId, string Date, double? Uncertainty);

/// <summary>고른 후보와 그 이유. <see cref="Score"/> 는 불확실도(확률이 아니다) — 무작위로 골랐으면 null.</summary>
public sealed record QuestionPick(QuestionCandidate Candidate, string Reason, double? Score);

/// <summary>학습용 출제 정책. <see cref="RandomFloor"/> 는 걸음 중 무작위로 낼 몫(0 ~ 1).</summary>
public sealed record QuestionPolicy(double RandomFloor);

/// <summary>출제 이유 — 문제 레코드에 남는다.</summary>
public static class QuestionReason
{
    /// <summary>기억 단이 가장 헷갈린 사진.</summary>
    public const string Uncertain = "uncertain";

    /// <summary>무작위 바닥 — 불확실한 것만 내면 풀이가 어려운 사진에 몰리므로 대표 표본을 섞는다.</summary>
    public const string RandomFloor = "random_floor";

    /// <summary>기억 단의 신호가 아직 없어(라벨이 모자람 · 기억 꺼짐) 무작위로 시작한다.</summary>
    public const string ColdStart = "cold_start";

    /// <summary>무작위 규칙(기본) — 모든 후보에서 균등하게 고른다.</summary>
    public const string Random = "random";
}

/// <summary>
/// 학습용 흐름의 출제 — 순수 함수. 후보 중 다음에 물을 사진 하나를 고른다. 규칙은 «기억 단이 가장 헷갈리는 사진»이고,
/// 걸음마다 <see cref="QuestionPolicy.RandomFloor"/> 의 확률로 대신 무작위 사진을 낸다. 기억 단의 신호가 있는 후보가 하나도
/// 없으면(콜드스타트) 무작위다. 어느 후보를 제외할지(이미 라벨이 있거나 최근에 물은 것)는 부르는 쪽이 정해 후보를 넘긴다.
/// </summary>
public static class QuestionSelector
{
    public static QuestionPick? Pick(IReadOnlyList<QuestionCandidate> candidates, QuestionPolicy policy, Random rng)
    {
        if (candidates.Count == 0)
            return null;

        var scored = candidates.Where(c => c.Uncertainty is not null).ToList();
        if (scored.Count == 0)
            return new(candidates[rng.Next(candidates.Count)], QuestionReason.ColdStart, null);

        if (policy.RandomFloor > 0 && rng.NextDouble() < policy.RandomFloor)
            return new(candidates[rng.Next(candidates.Count)], QuestionReason.RandomFloor, null);

        // 동률이면 먼저 온 것(입력 순서) — 같은 입력은 같은 답을 낸다.
        var top = scored.MaxBy(c => c.Uncertainty!.Value)!;
        return new(top, QuestionReason.Uncertain, top.Uncertainty);
    }
}

/// <summary>결정 장부의 기억 annotation 에서 불확실도를 뽑는다 — 순수 함수.</summary>
public static class QuestionSignals
{
    /// <summary>
    /// 이웃이 갈렸으면(최근접 거리는 남았는데 모인 클래스가 없다) 1 + 최근접 거리, 아니면 최근접 거리(멀수록 낯설다).
    /// 기억 단이 거리를 남기지 않았으면(기억 꺼짐 · 이웃 부족 · 임베딩 실패) null.
    /// </summary>
    public static double? UncertaintyFromAnnotations(IReadOnlyDictionary<string, string>? annotations)
    {
        if (annotations is null
            || !annotations.TryGetValue("memory:nearest_distance", out var raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance))
            return null;
        return annotations.ContainsKey("memory:agreed_class") ? distance : 1.0 + distance;
    }
}
