using UVision.Api.Models;
using UVision.Api.Services.DualCheck;

namespace UVision.Api.Services.Posture;

/// <summary>판정 verdict 의 출처(A1). MlPrimary 단계에서만 Ml.</summary>
public enum VerdictSource { Vlm, Ml }

/// <summary>단계별 운영 결정 — verdict 출처·posture·ml 노출 + 사유(로그·503·wire).</summary>
public sealed record AuthorityDecision
{
    public required VerdictSource VerdictSource { get; init; }
    public required InspectionPosture Posture { get; init; }
    /// <summary>응답에 ml/agreement/requires_review 를 실을지(shadow=false → byte-identical).</summary>
    public required bool SurfaceMl { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// 권한 이양 단계(A1) → 운영 결정을 산출하는 순수 함수. <see cref="DualCheckEvaluator"/>·
/// <c>DegradeLadder</c> 의 형제이며 후자를 <b>흡수</b>한다(posture+verdict 출처 단일 진실원).
/// 병렬 호출·예외 흡수·자동격하 오케스트레이션은 엔드포인트가 소유한다.
/// <para>
/// VLM 단계(Shadow/Advisory/CoPrimary): VLM=must-succeed 검출원. MlPrimary: 역할 스왑 —
/// ML=must-succeed, VLM=교차검증(대칭 반전).
/// </para>
/// </summary>
public static class AuthorityLadder
{
    public static AuthorityDecision Evaluate(
        AuthorityStage stage, bool vlmSucceeded, bool mlSucceeded, DualCheckResult? dual)
    {
        if (stage == AuthorityStage.MlPrimary)
            return EvaluateMlPrimary(vlmSucceeded, mlSucceeded, dual);

        // VLM-primary 단계들(Shadow/Advisory/CoPrimary) — VLM=must-succeed.
        if (!vlmSucceeded)
            return new()
            {
                VerdictSource = VerdictSource.Vlm,
                Posture = InspectionPosture.FailClosed,
                SurfaceMl = stage != AuthorityStage.Shadow,
                Reason = "vlm_unavailable",
            };

        var surfaceMl = stage != AuthorityStage.Shadow;
        var mismatch = dual?.RequiresReview == true;
        var posture = (stage, mismatch, surfaceMl) switch
        {
            (AuthorityStage.Shadow, _, _) => InspectionPosture.Proceed, // ml 숨김 → 비차단
            (AuthorityStage.CoPrimary, true, _) => InspectionPosture.ReviewBlock,
            (_, true, true) => InspectionPosture.ReviewHold, // Advisory 불일치
            _ => InspectionPosture.Proceed,
        };
        return new()
        {
            VerdictSource = VerdictSource.Vlm,
            Posture = posture,
            SurfaceMl = surfaceMl,
            Reason = posture == InspectionPosture.ReviewBlock
                ? "dual_check_mismatch_or_low_confidence"
                : posture == InspectionPosture.ReviewHold
                    ? "dual_check_mismatch_or_low_confidence"
                    : null,
        };
    }

    private static AuthorityDecision EvaluateMlPrimary(
        bool vlmSucceeded, bool mlSucceeded, DualCheckResult? dual)
    {
        if (!mlSucceeded) // 주 검출원(ML) 다운 → fail-closed
            return new()
            {
                VerdictSource = VerdictSource.Ml,
                Posture = InspectionPosture.FailClosed,
                SurfaceMl = true,
                Reason = "ml_unavailable",
            };
        if (!vlmSucceeded) // 교차검증(VLM) 다운 = degrade, ML 단독 진행
            return new()
            {
                VerdictSource = VerdictSource.Ml, Posture = InspectionPosture.Proceed, SurfaceMl = true,
            };
        var mismatch = dual?.RequiresReview == true;
        return new()
        {
            VerdictSource = VerdictSource.Ml,
            Posture = mismatch ? InspectionPosture.ReviewBlock : InspectionPosture.Proceed,
            SurfaceMl = true,
            Reason = mismatch ? "dual_check_mismatch_or_low_confidence" : null,
        };
    }
}
