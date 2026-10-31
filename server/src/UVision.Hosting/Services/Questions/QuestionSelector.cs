using System.Globalization;

namespace UVision.Api.Services.Questions;

/// <summary>
/// 아직 사람이 확정하지 않은 사진 하나 — 문제로 내보낼 수 있는 후보. <see cref="Uncertainty"/> 는 기억 단이 그 사진을 판정할 때
/// 얼마나 헷갈렸나(클수록 불확실)이고, 기억 단이 아무것도 남기지 않았으면 null. <see cref="PatchScore"/> 는 판정 때 장부에 남은
/// 패치 이상 점수(켜져 있었을 때만) — 패치 판정자가 있으면 불확실도는 이 점수와 문턱의 거리로 다시 잡는다(<see cref="QuestionSignals"/>).
/// </summary>
public sealed record QuestionCandidate(string ImageId, string Date, double? Uncertainty, double? PatchScore = null);

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

    /// <summary>정답을 아는 기준 이미지를 심었다 — 풀이자의 정답률을 재는 문제. 풀이자에게 보이는 응답은 이 값을 <see cref="Random"/> 으로 가린다.</summary>
    public const string Plant = "plant";
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

/// <summary>
/// 패치 판정자의 문턱 τ — 순수 함수. 문턱은 <b>대표 표본</b>(출제 이유가 무작위인 문제)에서 정상으로 답한 사진의 패치 점수로만 잡는다.
/// 불확실한 항목으로 모은 라벨은 어려운 정상이 몰려 문턱을 끌어올리므로 쓰지 않는다.
/// </summary>
public static class QuestionThreshold
{
    /// <summary>문턱을 잡는 데 필요한 대표 정상 라벨의 최소 수 — 이보다 적으면 문턱이 없고 출제는 무작위로 시작한다.</summary>
    public const int MinSamples = 10;

    /// <summary>문턱 = 대표 정상 점수의 이 백분위(0 ~ 1) — 정상의 95 % 를 정상쪽에 두는 합격선.</summary>
    public const double Percentile = 0.95;

    /// <summary>대표 표본으로 보는 출제 이유 — 후보 전체에서 균등하게(무작위 규칙 · 바닥 · 콜드스타트) 고른 것.</summary>
    public static bool IsRepresentative(string reason) =>
        reason is QuestionReason.Random or QuestionReason.RandomFloor or QuestionReason.ColdStart;

    /// <summary>선형 보간 백분위. 점수가 <see cref="MinSamples"/> 미만이면 null.</summary>
    public static double? Calibrate(IReadOnlyCollection<double> representativeOkScores)
    {
        if (representativeOkScores.Count < MinSamples)
            return null;
        var sorted = representativeOkScores.Order().ToArray();
        var rank = Percentile * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}

/// <summary>결정 장부의 기억 annotation 에서 불확실도를 뽑는다 — 순수 함수.</summary>
public static class QuestionSignals
{
    /// <summary>판정 때 장부에 남은 패치 이상 점수. 패치 모드가 꺼져 있었거나 점수가 없으면 null.</summary>
    public static double? PatchScoreFromAnnotations(IReadOnlyDictionary<string, string>? annotations) =>
        annotations is not null
        && annotations.TryGetValue("memory:patch_score", out var raw)
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
        && double.IsFinite(score)
            ? score
            : null;

    /// <summary>
    /// 패치 점수가 문턱에 가까울수록 크다(문턱에서 1, 멀어질수록 0 으로) — 순위용 값이지 확률이 아니다. 정상 / 불량 어느 쪽인지
    /// 판정자가 가장 헷갈리는 사진이 높게 나온다.
    /// </summary>
    public static double UncertaintyFromPatchScore(double score, double threshold) =>
        1.0 / (1.0 + Math.Abs(score - threshold));

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
