using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Oracle;
using Xunit;

namespace UVision.Tests;

public class OracleSweepPlannerTests
{
    private static StoredResult Result(bool? rr) => new()
    {
        ScenarioId = "sc", ImageId = "i", Verdict = Verdict.OK, Findings = "", Confidence = 0.9,
        Timestamp = "t", ImageFile = "i.jpg", DeviceId = "", DeviceLabel = "", RequiresReview = rr,
    };

    [Fact]
    public void NeedsOracle_RequiresReview_NoSidecar_True() =>
        Assert.True(OracleSweepPlanner.NeedsOracle(Result(true), null));

    [Fact]
    public void NeedsOracle_NotRequiresReview_False() =>
        Assert.False(OracleSweepPlanner.NeedsOracle(Result(false), null));

    [Fact]
    public void NeedsOracle_AlreadyOracled_False()
    {
        var sidecar = new StoredLabel
        {
            ImageId = "i", Label = null, Timestamp = "t",
            History = [new LabelEvent { Label = "NG", By = "oracle", At = "t", Mode = LabelMode.Oracle }],
        };
        Assert.False(OracleSweepPlanner.NeedsOracle(Result(true), sidecar));
    }

    [Fact]
    public void NeedsOracle_HumanLabeledNoOracle_True()
    {
        var sidecar = new StoredLabel
        {
            ImageId = "i", Label = "OK", Timestamp = "t",
            History = [new LabelEvent { Label = "OK", By = "d1", At = "t", Mode = LabelMode.Label }],
        };
        Assert.True(OracleSweepPlanner.NeedsOracle(Result(true), sidecar)); // 계통편향 교차검증
    }

    [Theory]
    [InlineData(false, false, true)]   // 로컬 = egress 아님 → 항상 허용
    [InlineData(true, false, false)]   // cloud + opt-out → 차단
    [InlineData(true, true, true)]     // cloud + opt-in → 허용
    public void EgressAllowed(bool cloud, bool optIn, bool expected) =>
        Assert.Equal(expected, OracleSweepPlanner.EgressAllowed(cloud, optIn));
}
