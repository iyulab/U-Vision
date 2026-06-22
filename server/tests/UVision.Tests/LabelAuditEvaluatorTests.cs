using UVision.Api.Services.Label;
using Xunit;

namespace UVision.Tests;

public class LabelAuditEvaluatorTests
{
    [Fact]
    public void StableHash_IsDeterministic_AndPlatformStable()
    {
        // FNV-1a 고정 알고리즘 — 런타임 무관 안정값(string.GetHashCode 는 부적합).
        Assert.Equal(LabelAuditEvaluator.StableHash("img_abc"), LabelAuditEvaluator.StableHash("img_abc"));
        Assert.NotEqual(LabelAuditEvaluator.StableHash("img_abc"), LabelAuditEvaluator.StableHash("img_xyz"));
    }

    [Fact]
    public void IsSampled_ZeroRate_NeverSamples()
    {
        Assert.False(LabelAuditEvaluator.IsSampled("img_abc", 0));
    }

    [Fact]
    public void IsSampled_HundredRate_AlwaysSamples()
    {
        Assert.True(LabelAuditEvaluator.IsSampled("img_abc", 100));
        Assert.True(LabelAuditEvaluator.IsSampled("anything", 100));
    }

    [Fact]
    public void IsSampled_IsMonotonic_HigherRateIsSuperset()
    {
        // 같은 id 가 낮은 k 에서 뽑히면 높은 k 에서도 뽑힌다(임계 단조성).
        string[] ids = ["a", "b", "c", "d", "e", "f", "g", "h"];
        var at30 = ids.Where(i => LabelAuditEvaluator.IsSampled(i, 30)).ToHashSet();
        var at60 = ids.Where(i => LabelAuditEvaluator.IsSampled(i, 60)).ToHashSet();
        Assert.True(at30.IsSubsetOf(at60)); // at30 ⊆ at60
    }

    [Fact]
    public void EvaluateAuditStatus_SameLabel_IsConsistent()
    {
        Assert.Equal(LabelAuditStatus.Consistent, LabelAuditEvaluator.EvaluateAuditStatus("NG", "NG"));
    }

    [Fact]
    public void EvaluateAuditStatus_DifferentLabel_IsConflicted()
    {
        Assert.Equal(LabelAuditStatus.Conflicted, LabelAuditEvaluator.EvaluateAuditStatus("OK", "NG"));
    }
}
