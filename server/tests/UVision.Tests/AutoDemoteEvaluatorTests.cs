using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Authority;
using Xunit;

namespace UVision.Tests;

public class AutoDemoteEvaluatorTests
{
    private static MetricsSummary Summary(int inspections, double? mlRecall, double? vlmRecall) => new()
    {
        ScenarioId = "demo", Date = "2026-06-23",
        Inspections = inspections, MlDegraded = 0, FailClosed = 0, Agreements = 0, ReviewsRequired = 0,
        Audited = 0, LabelConsistent = 0, LabelConflictsOpen = 0,
        Labeled = inspections, LabeledNg = inspections, VlmNgHits = 0, MlNgScored = inspections, MlNgHits = 0,
        MlNgRecall = mlRecall, VlmNgRecall = vlmRecall,
    };

    [Fact]
    public void CoPrimary_MlBelowVlm_DemotesToAdvisory()
    {
        var t = AutoDemoteEvaluator.Evaluate(AuthorityStage.CoPrimary,
            Summary(60, mlRecall: 0.8, vlmRecall: 0.95), new AuthorityOptions());
        Assert.Equal(AuthorityStage.Advisory, t);
    }

    [Fact]
    public void MlPrimary_MlBelowFloor_DemotesToCoPrimary()
    {
        var t = AutoDemoteEvaluator.Evaluate(AuthorityStage.MlPrimary,
            Summary(60, mlRecall: 0.9, vlmRecall: 0.85), new AuthorityOptions()); // 0.9 < RecallFloor 0.95
        Assert.Equal(AuthorityStage.CoPrimary, t);
    }

    [Fact]
    public void CoPrimary_Healthy_NoDemote()
    {
        var t = AutoDemoteEvaluator.Evaluate(AuthorityStage.CoPrimary,
            Summary(60, mlRecall: 0.97, vlmRecall: 0.95), new AuthorityOptions());
        Assert.Null(t);
    }

    [Fact]
    public void SmallWindow_NoDemote() =>
        Assert.Null(AutoDemoteEvaluator.Evaluate(AuthorityStage.MlPrimary,
            Summary(10, mlRecall: 0.5, vlmRecall: 0.95), new AuthorityOptions()));

    [Fact]
    public void Advisory_NeverDemotes() =>
        Assert.Null(AutoDemoteEvaluator.Evaluate(AuthorityStage.Advisory,
            Summary(60, mlRecall: 0.1, vlmRecall: 0.95), new AuthorityOptions()));

    [Fact]
    public void NullRecall_NoDemote() =>
        Assert.Null(AutoDemoteEvaluator.Evaluate(AuthorityStage.CoPrimary,
            Summary(60, mlRecall: null, vlmRecall: 0.95), new AuthorityOptions()));
}
