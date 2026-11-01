using UVision.Api.Models;

namespace UVision.Api.Services.Memory;

/// <summary>
/// 기억 단 문턱 추천 한 건 — <see cref="MaxDistance"/> 가 null 이면 추천할 근거가 없는 것이고 <see cref="Reason"/> 이 왜인지 말한다.
/// </summary>
/// <param name="MaxDistance">추천하는 최근접 코사인 거리 상한(없으면 null).</param>
/// <param name="Answered">그 문턱에서 기억이 답했을 판정 수.</param>
/// <param name="LabeledDefects">재생한 판정 가운데 사람 라벨이 불량인 수 — 증거의 크기.</param>
/// <param name="Missed">그 문턱에서 불량이 OK 로 지나간 수(추천은 언제나 0).</param>
/// <param name="DefectPassUpper95">불량 통과율의 단측 95 % 상한 — 놓침 0 이어도 불량이 적으면 크게 남는다.</param>
/// <param name="Sufficient">그 상한이 목표 놓침률 안인가 — 아니면 추천은 후보일 뿐 증거가 모자라다.</param>
/// <param name="DefectsNeeded">놓침 0 으로 목표를 95 % 신뢰로 주장하는 데 필요한 불량 라벨 수(총).</param>
public sealed record OptionAdvice(
    double? MaxDistance, int Answered, int LabeledDefects, int Missed,
    double? DefectPassUpper95, bool Sufficient, int DefectsNeeded, string Reason);

/// <summary>
/// 기억 단 옵션 추천 — 순수 함수. 문턱 재생 표(<see cref="MemoryReplay"/>)에서 «사람이 불량이라 확정한 사진이 한 장도 OK 로
/// 지나가지 않은 가장 큰 문턱» 을 고르고, 그 주장이 얼마나 단단한지를 함께 낸다.
/// <para>
/// 놓침 0 은 증거가 아니라 관찰이다 — 불량이 6 장뿐이면 놓침 0 이어도 통과율은 39 % 까지 가능하다. 그래서 정확 이항의 단측 95 %
/// 상한(<c>1 − 0.05^(1/n)</c>)을 같이 내고, 그 상한이 목표 안일 때만 <see cref="OptionAdvice.Sufficient"/> 다(관문의 합격 통계량과
/// 같은 사고 — 점추정이 아니라 신뢰 상한). 추천은 반영이 아니라 제안이다 — 적용은 사람이 한다.
/// </para>
/// </summary>
public static class OptionAdvisor
{
    /// <summary>상한의 신뢰 수준 — 놓침 0 의 정확 이항 상한은 <c>1 − (1 − 신뢰)^(1/n)</c>.</summary>
    public const double Confidence = 0.95;

    /// <param name="replay">문턱 재생 표(작은 문턱부터 — <see cref="MemoryReplay.Run"/> 의 결과).</param>
    /// <param name="targetMissRate">불량 통과율 목표(0 초과 1 미만 — 시나리오 <c>trust</c> 의 누락 상한, 기본 0.02).</param>
    public static OptionAdvice AdviseMaxDistance(IReadOnlyList<MemoryReplayRow> replay, double targetMissRate)
    {
        if (!(targetMissRate > 0 && targetMissRate < 1))
            throw new ArgumentOutOfRangeException(nameof(targetMissRate), "목표 놓침률은 0 초과 1 미만이어야 합니다");
        var needed = (int)Math.Ceiling(Math.Log(1 - Confidence) / Math.Log(1 - targetMissRate));

        if (replay.Count == 0)
            return None(0, 0, needed, "재생할 판정이 없다 — 기억이 거리를 남긴 판정이 아직 없다.");

        var defects = replay[0].LabeledDefects;   // 문턱과 무관하게 같은 분모
        if (defects == 0)
            return None(0, 0, needed, "사람이 확정한 불량 라벨이 없어 놓침을 잴 수 없다 — 불량 라벨이 먼저 필요하다.");

        var safe = replay.Where(r => r.Missed == 0).ToList();
        if (safe.Count == 0)
            return None(0, defects, needed, "모든 문턱에서 불량이 OK 로 답해졌다 — 기억에 맡길 수 있는 문턱이 사다리 안에 없다.");

        var best = safe[^1];   // 작은 문턱부터 정렬되어 있고 놓침은 문턱에 단조 증가 — 마지막이 가장 크다
        if (best.Answered == 0)
            return None(0, defects, needed, "놓침은 없지만 기억이 답한 판정도 없다 — 추천할 문턱이 없다.");

        var upper = 1 - Math.Pow(1 - Confidence, 1.0 / defects);
        var sufficient = upper <= targetMissRate;
        var reason = new System.Text.StringBuilder()
            .Append(System.Globalization.CultureInfo.InvariantCulture, $"문턱 {best.MaxDistance:0.###} 까지 불량 {defects}장 중 OK 로 지나간 것이 없다")
            .Append(System.Globalization.CultureInfo.InvariantCulture, $" — 불량 통과율의 95 % 상한은 {upper:P1}.");
        if (!sufficient)
            reason.Append(System.Globalization.CultureInfo.InvariantCulture, $" 목표 {targetMissRate:P1} 를 주장하려면 놓침 0 으로 불량 라벨 {needed}장이 필요하다 — 아직 후보일 뿐이다.");
        if (best == replay[^1])
            reason.Append(" 사다리 끝까지 놓침이 없어 더 큰 문턱은 재지 않았다.");

        return new OptionAdvice(best.MaxDistance, best.Answered, defects, best.Missed, upper, sufficient, needed, reason.ToString());
    }

    private static OptionAdvice None(int answered, int defects, int needed, string reason) =>
        new(null, answered, defects, 0, null, false, needed, reason);
}
