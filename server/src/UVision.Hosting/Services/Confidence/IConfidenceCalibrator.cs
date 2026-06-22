namespace UVision.Api.Services.Confidence;

/// <summary>
/// confidence 의 출처. 각 출처는 서로 비교 불가능한 척도다(VLM self-report·ML softmax·오라클).
/// 닫힌 집합 — 라벨(class-agnostic string)과 달리 표준화 대상은 유한·타입 안전.
/// </summary>
public enum ConfidenceSource { Vlm, Ml, Oracle }

/// <summary>
/// 판정원별 raw confidence 를 [0,1] 공통 '표준 신뢰 점수'로 변환한다(A3).
/// 이게 있어야 ③ 임계가 사과↔오렌지 비교를 벗어난다.
/// <para>
/// 콜드스타트 구현은 정적 변환(<see cref="StaticConfidenceCalibrator"/>)이고,
/// B3 메트릭이 쌓이면 데이터 기반 per-source 캘리브레이션 맵 구현체로 무중단 교체한다
/// (이 인터페이스가 그 seam).
/// </para>
/// </summary>
public interface IConfidenceCalibrator
{
    /// <summary>raw confidence → [0,1] 표준 신뢰 점수.</summary>
    double Standardize(ConfidenceSource source, double rawConfidence);
}
