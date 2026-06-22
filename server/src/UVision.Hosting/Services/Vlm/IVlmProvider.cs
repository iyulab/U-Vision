using UVision.Api.Models;

namespace UVision.Api.Services.Vlm;

/// <summary>
/// 비전 판정 provider 경계. 앱 코드는 이 인터페이스만 안다.
/// 구체 provider(mock/ironhive/…)는 <see cref="VlmProviderFactory"/> 가 설정에 따라 주입한다.
/// (원본: server/app/services/vlm/base.py)
/// </summary>
public interface IVlmProvider
{
    /// <summary>provider 식별자(mock/openai/google …).</summary>
    string Name { get; }

    /// <summary>이미지 1장을 시나리오 컨텍스트로 판정한다.</summary>
    Task<InspectionResult> InspectAsync(
        ReadOnlyMemory<byte> image,
        ScenarioContext scenario,
        CancellationToken cancellationToken = default);
}
