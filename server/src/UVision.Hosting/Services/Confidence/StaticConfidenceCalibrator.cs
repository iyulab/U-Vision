namespace UVision.Api.Services.Confidence;

/// <summary>
/// 콜드스타트(캘리브레이션 데이터 0) 기본 변환 — 키·히스토리 없이 동작.
/// VLM self-report 는 악명 높게 miscalibrated → <b>게이팅에서 제외</b>(상수 1.0, 항상 임계 통과).
/// ML softmax 는 그대로 통과(방어 clamp). '못 믿는 걸 안 믿는다'(헌법 정직성).
/// </summary>
public sealed class StaticConfidenceCalibrator : IConfidenceCalibrator
{
    public double Standardize(ConfidenceSource source, double rawConfidence) => source switch
    {
        ConfidenceSource.Vlm => 1.0,
        _ => Math.Clamp(rawConfidence, 0.0, 1.0),
    };
}
