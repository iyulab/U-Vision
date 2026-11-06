using UVision.Api.Models;
using UVision.Api.Services.Authority;
using Xunit;

namespace UVision.Tests;

/// <summary>격상에 명시 확인이 필요한가 — 순수 판정(D-116).</summary>
public class AuthorityPromotionGuardTests
{
    [Theory]
    [InlineData(AuthorityStage.Shadow, AuthorityStage.Advisory, null)]   // ML 이 판정 권한을 받지 않는다
    [InlineData(AuthorityStage.Shadow, AuthorityStage.Advisory, false)]
    [InlineData(AuthorityStage.Advisory, AuthorityStage.CoPrimary, true)] // 한 단계 + 적격
    [InlineData(AuthorityStage.CoPrimary, AuthorityStage.MlPrimary, true)]
    public void NoConfirmation_WhenTheMlGainsNoAuthority_OrTheSignalIsMetOnASingleStep(AuthorityStage from, AuthorityStage to, bool? eligible) =>
        Assert.Empty(AuthorityPromotionGuard.UnverifiedBasis(from, to, eligible));

    [Theory]
    [InlineData(false, "signal_unmet")]
    [InlineData(null, "signal_insufficient")]
    public void Confirmation_WhenTheMlGainsAuthority_AndTheSignalIsNotMet(bool? eligible, string expected) =>
        Assert.Equal([expected], AuthorityPromotionGuard.UnverifiedBasis(AuthorityStage.Advisory, AuthorityStage.CoPrimary, eligible));

    [Fact]
    public void Skipping_AlwaysNeedsConfirmation_EvenWhenEligible_AndListsBothReasonsWhenAlsoUnmet()
    {
        Assert.Equal(["skipped_stage"], AuthorityPromotionGuard.UnverifiedBasis(AuthorityStage.Advisory, AuthorityStage.MlPrimary, true));
        Assert.Equal(["skipped_stage", "signal_unmet"], AuthorityPromotionGuard.UnverifiedBasis(AuthorityStage.Advisory, AuthorityStage.MlPrimary, false));
        // shadow → co_primary 도 한 단계를 건너뛴다.
        Assert.Contains("skipped_stage", AuthorityPromotionGuard.UnverifiedBasis(AuthorityStage.Shadow, AuthorityStage.CoPrimary, true));
    }

    [Fact]
    public void Explain_TellsWhatToSend()
    {
        var text = AuthorityPromotionGuard.Explain(["skipped_stage", "signal_insufficient"]);
        Assert.Contains("건너뛰", text);
        Assert.Contains("표본", text);
        Assert.Contains("acknowledge_unverified: true", text);
    }
}
