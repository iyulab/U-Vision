using UVision.Api.Models;
using UVision.Api.Services.DualCheck;
using UVision.Api.Services.Posture;
using Xunit;

namespace UVision.Tests;

public class AuthorityLadderTests
{
    private static DualCheckResult Dual(bool requiresReview) => new()
    {
        MlLabel = "ok", MlConfidence = 0.9, Agreement = !requiresReview, RequiresReview = requiresReview,
    };

    // --- Shadow: VLM verdict, ml 숨김, 메트릭은 호출측이 기록 ---
    [Fact]
    public void Shadow_HidesMl_Proceeds()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.Shadow, true, true, Dual(requiresReview: true));
        Assert.Equal(VerdictSource.Vlm, d.VerdictSource);
        Assert.False(d.SurfaceMl);
        Assert.Equal(InspectionPosture.Proceed, d.Posture); // 불일치여도 숨김 → 비차단
    }

    [Fact]
    public void Shadow_VlmDown_IsFailClosed()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.Shadow, false, true, null);
        Assert.Equal(InspectionPosture.FailClosed, d.Posture);
    }

    // --- Advisory: 오늘 DegradeLadder 와 동치(불변 가드) ---
    [Fact]
    public void Advisory_Mismatch_IsReviewHold_VlmVerdict_SurfaceMl()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.Advisory, true, true, Dual(requiresReview: true));
        Assert.Equal(VerdictSource.Vlm, d.VerdictSource);
        Assert.True(d.SurfaceMl);
        Assert.Equal(InspectionPosture.ReviewHold, d.Posture);
    }

    [Fact]
    public void Advisory_Agree_IsProceed()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.Advisory, true, true, Dual(requiresReview: false));
        Assert.Equal(InspectionPosture.Proceed, d.Posture);
    }

    [Fact]
    public void Advisory_VlmDown_IsFailClosed()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.Advisory, false, true, null);
        Assert.Equal(InspectionPosture.FailClosed, d.Posture);
        Assert.Equal("vlm_unavailable", d.Reason);
    }

    // --- CoPrimary: 불일치 = 차단 게이트 ---
    [Fact]
    public void CoPrimary_Mismatch_IsReviewBlock_VlmVerdict()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.CoPrimary, true, true, Dual(requiresReview: true));
        Assert.Equal(VerdictSource.Vlm, d.VerdictSource);
        Assert.Equal(InspectionPosture.ReviewBlock, d.Posture);
    }

    [Fact]
    public void CoPrimary_Agree_IsProceed()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.CoPrimary, true, true, Dual(requiresReview: false));
        Assert.Equal(InspectionPosture.Proceed, d.Posture);
    }

    [Fact]
    public void CoPrimary_VlmDown_IsFailClosed()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.CoPrimary, false, true, null);
        Assert.Equal(InspectionPosture.FailClosed, d.Posture);
    }

    // --- MlPrimary: 역할 스왑(대칭 반전) ---
    [Fact]
    public void MlPrimary_BothOk_Agree_MlVerdict_Proceed()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.MlPrimary, true, true, Dual(requiresReview: false));
        Assert.Equal(VerdictSource.Ml, d.VerdictSource);
        Assert.Equal(InspectionPosture.Proceed, d.Posture);
    }

    [Fact]
    public void MlPrimary_Mismatch_IsReviewBlock()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.MlPrimary, true, true, Dual(requiresReview: true));
        Assert.Equal(InspectionPosture.ReviewBlock, d.Posture);
    }

    [Fact]
    public void MlPrimary_MlDown_IsFailClosed()
    {
        var d = AuthorityLadder.Evaluate(AuthorityStage.MlPrimary, true, false, null);
        Assert.Equal(InspectionPosture.FailClosed, d.Posture);
        Assert.Equal("ml_unavailable", d.Reason);
    }

    [Fact]
    public void MlPrimary_VlmDown_MlUp_Proceeds_MlSolo()
    {
        // 교차검증(VLM) 다운 = degrade, 주 검출원 ML 단독 진행.
        var d = AuthorityLadder.Evaluate(AuthorityStage.MlPrimary, false, true, null);
        Assert.Equal(VerdictSource.Ml, d.VerdictSource);
        Assert.Equal(InspectionPosture.Proceed, d.Posture);
    }
}
