using UVision.Api.Models;
using UVision.Api.Services.Confidence;
using UVision.Api.Services.DualCheck;
using UVision.Api.Services.Ml;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// ③ 2중체크 평가기(순수 함수) — 일치/불일치·표준화 후 저신뢰 게이팅·class-agnostic 라벨 비교.
/// A3: VLM·ML confidence 는 calibrator 로 표준화 후 비교한다(콜드스타트엔 VLM 게이팅 제외).
/// </summary>
public class DualCheckEvaluatorTests
{
    private static readonly IConfidenceCalibrator Cal = new StaticConfidenceCalibrator();

    private static InspectionResult Vlm(Verdict v, double conf = 0.9) =>
        new() { Verdict = v, Confidence = conf, Findings = "" };

    private static MlClassification Ml(string label, double conf = 0.9) =>
        new() { Label = label, Confidence = conf };

    [Theory]
    [InlineData(Verdict.OK, "ok")]
    [InlineData(Verdict.NG, "ng")]
    [InlineData(Verdict.OK, "OK")]
    [InlineData(Verdict.NG, "NG")]
    public void Agreement_WhenSameClass_True_AndNoReview(Verdict v, string label)
    {
        var r = DualCheckEvaluator.Evaluate(Vlm(v), Ml(label), reviewThreshold: 0.0, Cal);
        Assert.True(r.Agreement);
        Assert.False(r.RequiresReview);
    }

    [Theory]
    [InlineData(Verdict.OK, "ng")]
    [InlineData(Verdict.NG, "ok")]
    public void Disagreement_RequiresReview(Verdict v, string label)
    {
        var r = DualCheckEvaluator.Evaluate(Vlm(v), Ml(label), reviewThreshold: 0.0, Cal);
        Assert.False(r.Agreement);
        Assert.True(r.RequiresReview);
    }

    [Fact]
    public void UnrecognizedMlLabel_TreatedAsDisagreement()
    {
        var r = DualCheckEvaluator.Evaluate(Vlm(Verdict.OK), Ml("scratch"), reviewThreshold: 0.0, Cal);
        Assert.False(r.Agreement);
        Assert.True(r.RequiresReview);
    }

    [Fact]
    public void Agreement_LowVlmConfidence_DoesNotRequireReview_VlmExcludedFromGating()
    {
        // A3 신규: VLM raw 가 임계 미만이어도 표준화(=1.0)로 게이팅 제외 → 검토 유발 안 함.
        // (기존 동작에선 검토를 유발했음 — self-report 를 믿지 않게 바뀐 핵심 차이.)
        var r = DualCheckEvaluator.Evaluate(
            Vlm(Verdict.OK, conf: 0.50), Ml("ok", conf: 0.99), reviewThreshold: 0.80, Cal);
        Assert.True(r.Agreement);
        Assert.False(r.RequiresReview);
    }

    [Fact]
    public void Agreement_LowMlConfidence_RequiresReview()
    {
        // ML softmax 는 표준화 통과 → 임계 미만이면 검토 유발(게이팅 유지).
        var r = DualCheckEvaluator.Evaluate(
            Vlm(Verdict.OK, conf: 0.99), Ml("ok", conf: 0.50), reviewThreshold: 0.80, Cal);
        Assert.True(r.Agreement);
        Assert.True(r.RequiresReview);
    }

    [Fact]
    public void Agreement_BothConfident_AboveThreshold_NoReview()
    {
        var r = DualCheckEvaluator.Evaluate(
            Vlm(Verdict.OK, conf: 0.95), Ml("ok", conf: 0.95), reviewThreshold: 0.80, Cal);
        Assert.True(r.Agreement);
        Assert.False(r.RequiresReview);
    }

    [Fact]
    public void Threshold_Zero_DisablesGating_ByteIdentical()
    {
        // 회귀 가드: 임계 0 이면 표준화 무관하게 불일치만 검토(기존 동작 보존).
        var r = DualCheckEvaluator.Evaluate(
            Vlm(Verdict.OK, conf: 0.01), Ml("ok", conf: 0.01), reviewThreshold: 0.0, Cal);
        Assert.True(r.Agreement);
        Assert.False(r.RequiresReview);
    }

    [Fact]
    public void Result_CarriesMlLabelAndRawConfidence()
    {
        // MlConfidence 는 raw 유지(표준화는 내부 게이팅에만).
        var r = DualCheckEvaluator.Evaluate(Vlm(Verdict.NG), Ml("ng", conf: 0.77), reviewThreshold: 0.0, Cal);
        Assert.Equal("ng", r.MlLabel);
        Assert.Equal(0.77, r.MlConfidence);
    }
}
