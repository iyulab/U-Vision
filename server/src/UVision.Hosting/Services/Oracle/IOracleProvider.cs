using UVision.Api.Models;

namespace UVision.Api.Services.Oracle;

/// <summary>
/// 오라클("더 센 VLM") 2차 의견 경계 — IVlmProvider·IMlClassifier 형제(④-B).
/// 비동기 라벨생성기/2차 의견이지 운영 verdict 타이브레이커가 아니다(핫패스 밖, 스윕이 호출).
/// </summary>
public interface IOracleProvider
{
    string Name { get; }
    bool IsEnabled { get; }   // none → false. 스윕이 게이트.
    bool IsCloud { get; }     // gpustack → false(egress 아님). cloud(미래) → true.

    /// <summary>이미지 1장에 2차 소견을 낸다. UNVERIFIED: 실 gpustack 왕복은 모델/키 확보 후 검증.</summary>
    Task<InspectionResult> SecondOpinionAsync(
        ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken cancellationToken = default);
}
