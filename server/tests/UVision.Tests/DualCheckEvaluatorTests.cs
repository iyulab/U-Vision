using System.Collections.Generic;
using UVision.Api.Models;
using UVision.Api.Services.DualCheck;
using UVision.Api.Services.Ml;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// ③ 2중체크 평가기(순수 함수) — 일치/불일치·저신뢰 게이팅·class-agnostic 라벨 비교.
/// 병렬 호출은 엔드포인트가 소유하고, 여기서는 비교/스코어링 로직만 결정론적으로 검증한다.
/// </summary>
public class DualCheckEvaluatorTests
{
    private static InspectionResult Vlm(Verdict v, double conf = 0.9) =>
        new() { Verdict = v, Confidence = conf, Findings = "" };

    private static MlClassification Ml(string label, double conf = 0.9) =>
        new() { Label = label, Confidence = conf };

    [Theory]
    [InlineData(Verdict.OK, "ok")]
    [InlineData(Verdict.NG, "ng")]
    [InlineData(Verdict.OK, "OK")]   // 대소문자 무시(ML 라벨 "ok" vs verdict "OK")
    [InlineData(Verdict.NG, "NG")]
    public void Agreement_WhenSameClass_True_AndNoReview(Verdict v, string label)
    {
        var r = DualCheckEvaluator.Evaluate(Vlm(v), Ml(label), reviewThreshold: 0.0);
        Assert.True(r.Agreement);
        Assert.False(r.RequiresReview); // 일치 + 임계 0(게이팅 off) → 자동확정
    }

    [Theory]
    [InlineData(Verdict.OK, "ng")]
    [InlineData(Verdict.NG, "ok")]
    public void Disagreement_RequiresReview(Verdict v, string label)
    {
        var r = DualCheckEvaluator.Evaluate(Vlm(v), Ml(label), reviewThreshold: 0.0);
        Assert.False(r.Agreement);
        Assert.True(r.RequiresReview); // 불일치는 임계와 무관하게 항상 검토
    }

    [Fact]
    public void UnrecognizedMlLabel_TreatedAsDisagreement()
    {
        // class-agnostic: 알 수 없는 라벨은 verdict 와 다른 문자열 → 불일치 → 검토.
        var r = DualCheckEvaluator.Evaluate(Vlm(Verdict.OK), Ml("scratch"), reviewThreshold: 0.0);
        Assert.False(r.Agreement);
        Assert.True(r.RequiresReview);
    }

    [Fact]
    public void Agreement_ButLowVlmConfidence_RequiresReview()
    {
        var r = DualCheckEvaluator.Evaluate(
            Vlm(Verdict.OK, conf: 0.50), Ml("ok", conf: 0.99), reviewThreshold: 0.80);
        Assert.True(r.Agreement);
        Assert.True(r.RequiresReview); // 일치해도 VLM 저신뢰 → 검토
    }

    [Fact]
    public void Agreement_ButLowMlConfidence_RequiresReview()
    {
        var r = DualCheckEvaluator.Evaluate(
            Vlm(Verdict.OK, conf: 0.99), Ml("ok", conf: 0.50), reviewThreshold: 0.80);
        Assert.True(r.Agreement);
        Assert.True(r.RequiresReview); // 일치해도 ML 저신뢰 → 검토
    }

    [Fact]
    public void Agreement_BothConfident_AboveThreshold_NoReview()
    {
        var r = DualCheckEvaluator.Evaluate(
            Vlm(Verdict.OK, conf: 0.95), Ml("ok", conf: 0.95), reviewThreshold: 0.80);
        Assert.True(r.Agreement);
        Assert.False(r.RequiresReview);
    }

    [Fact]
    public void Result_CarriesMlLabelAndConfidence()
    {
        var r = DualCheckEvaluator.Evaluate(Vlm(Verdict.NG), Ml("ng", conf: 0.77), reviewThreshold: 0.0);
        Assert.Equal("ng", r.MlLabel);
        Assert.Equal(0.77, r.MlConfidence);
    }
}
