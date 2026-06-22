using UVision.Api.Services.Confidence;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// A3 콜드스타트 정적 변환 — VLM self-report 는 게이팅 제외(상수 1.0),
/// ML softmax 는 그대로(방어 clamp). source 간 raw 절대값 직접비교 금지의 구현.
/// </summary>
public class StaticConfidenceCalibratorTests
{
    private readonly StaticConfidenceCalibrator _cal = new();

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.50)]
    [InlineData(0.90)]
    public void Vlm_AlwaysStandardizesToOne_RegardlessOfRaw(double raw)
    {
        // VLM self-report 는 miscalibrated → 신뢰 게이팅에서 제외(항상 '충분').
        Assert.Equal(1.0, _cal.Standardize(ConfidenceSource.Vlm, raw));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Ml_PassesSoftmaxThrough(double raw)
    {
        Assert.Equal(raw, _cal.Standardize(ConfidenceSource.Ml, raw));
    }

    [Theory]
    [InlineData(1.5, 1.0)]   // 상한 clamp
    [InlineData(-0.2, 0.0)]  // 하한 clamp
    public void Ml_ClampsOutOfRange(double raw, double expected)
    {
        Assert.Equal(expected, _cal.Standardize(ConfidenceSource.Ml, raw));
    }
}
