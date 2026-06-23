using UVision.Api.Services.DualCheck;

namespace UVision.Api.Services.Posture;

/// <summary>운영 자세 등급(신뢰성 플라이휠 ③.5 E2 열화 사다리).</summary>
public enum InspectionPosture
{
    /// <summary>정상 진행 — VLM verdict 자동 확정(ML 일치/없음).</summary>
    Proceed,

    /// <summary>VLM verdict는 유효하나 ML 불일치/저신뢰 → 운영 화면 비차단 '검토 필요'(advisory).</summary>
    ReviewHold,

    /// <summary>불일치(co-primary↑) → 운영 화면 차단형 확인 게이트(작업자 명시 확인 전 진행 차단, A1).</summary>
    ReviewBlock,

    /// <summary>주 검출원(VLM) 사용 불가 → 자동 판정 없음, 사람 확인 필요(NG-safe fail-closed).</summary>
    FailClosed,
}

/// <summary>자세 결정 + 사유(로그·메트릭·503 본문용).</summary>
public sealed record PostureDecision
{
    public required InspectionPosture Posture { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// (VLM 성공 여부, 2중체크 결과) → 운영 자세를 결정하는 순수 함수(결정론·테스트 용이).
/// 병렬 호출·예외 처리는 엔드포인트가 소유한다(<see cref="DualCheckEvaluator"/> 와 동일 규율).
/// VLM 은 must-succeed 검출원 — 실패 시 ML 이 있어도 verdict 로 승격하지 않는다(advisory 단계).
/// </summary>
public static class DegradeLadder
{
    public static PostureDecision Evaluate(bool vlmSucceeded, DualCheckResult? dual)
    {
        if (!vlmSucceeded)
            return new() { Posture = InspectionPosture.FailClosed, Reason = "vlm_unavailable" };
        if (dual?.RequiresReview == true)
            return new()
            {
                Posture = InspectionPosture.ReviewHold,
                Reason = "dual_check_mismatch_or_low_confidence",
            };
        return new() { Posture = InspectionPosture.Proceed };
    }
}
