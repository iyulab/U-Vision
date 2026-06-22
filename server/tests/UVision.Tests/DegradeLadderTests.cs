using UVision.Api.Services.DualCheck;
using UVision.Api.Services.Posture;
using Xunit;

namespace UVision.Tests;

public class DegradeLadderTests
{
    private static DualCheckResult Dual(bool requiresReview) => new()
    {
        MlLabel = "ok", MlConfidence = 0.9, Agreement = !requiresReview, RequiresReview = requiresReview,
    };

    [Fact]
    public void VlmFailed_IsFailClosed_WithReason()
    {
        var d = DegradeLadder.Evaluate(vlmSucceeded: false, dual: null);
        Assert.Equal(InspectionPosture.FailClosed, d.Posture);
        Assert.Equal("vlm_unavailable", d.Reason);
    }

    [Fact]
    public void VlmFailed_IsFailClosed_EvenWhenMlPresent()
    {
        var d = DegradeLadder.Evaluate(vlmSucceeded: false, dual: Dual(requiresReview: false));
        Assert.Equal(InspectionPosture.FailClosed, d.Posture);
    }

    [Fact]
    public void VlmOk_NoDual_IsProceed()
    {
        var d = DegradeLadder.Evaluate(vlmSucceeded: true, dual: null);
        Assert.Equal(InspectionPosture.Proceed, d.Posture);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void VlmOk_DualAgrees_IsProceed()
    {
        var d = DegradeLadder.Evaluate(vlmSucceeded: true, dual: Dual(requiresReview: false));
        Assert.Equal(InspectionPosture.Proceed, d.Posture);
    }

    [Fact]
    public void VlmOk_DualRequiresReview_IsReviewHold()
    {
        var d = DegradeLadder.Evaluate(vlmSucceeded: true, dual: Dual(requiresReview: true));
        Assert.Equal(InspectionPosture.ReviewHold, d.Posture);
        Assert.Equal("dual_check_mismatch_or_low_confidence", d.Reason);
    }
}
