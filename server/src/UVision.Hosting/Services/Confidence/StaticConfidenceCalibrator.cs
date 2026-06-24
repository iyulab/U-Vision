namespace UVision.Api.Services.Confidence;

/// <summary>
/// 콜드스타트(캘리브레이션 데이터 0) 기본 변환 — 키·히스토리 없이 동작.
/// VLM·Oracle self-report 는 악명 높게 miscalibrated → <b>게이팅에서 제외</b>(상수 1.0, 항상 임계 통과).
/// ML softmax 는 그대로 통과(방어 clamp). '못 믿는 걸 안 믿는다'(헌법 정직성).
/// <para>Oracle 은 고성능 VLM 계열(IronHiveVlmProvider 재사용)이라 self-report 신뢰도도 VLM 과 동급으로
/// 게이팅 제외한다 — ML softmax 와 같은 척도가 아니다(④-B 배선 시 self-report 를 신뢰하지 않도록 명시).</para>
/// </summary>
public sealed class StaticConfidenceCalibrator : IConfidenceCalibrator
{
    public double Standardize(ConfidenceSource source, double rawConfidence) => source switch
    {
        ConfidenceSource.Vlm or ConfidenceSource.Oracle => 1.0,
        _ => Math.Clamp(rawConfidence, 0.0, 1.0),
    };
}
