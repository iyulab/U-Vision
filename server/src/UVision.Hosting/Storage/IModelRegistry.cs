using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 시나리오별 모델 버전 레지스트리(신뢰성 플라이휠 B1). 버전 디렉터리(불변 manifest)와 active 포인터를
/// 소유한다. 모델 바이너리는 MLoop serve 소유 — 여기서는 참조·이력만 다룬다.
/// </summary>
public interface IModelRegistry
{
    /// <summary>새 버전을 등록한다(active 불변). 채번된 version("v{n}") 반환.</summary>
    Task<string> RegisterAsync(
        string scenarioId, ModelRegistration registration, CancellationToken ct = default);

    /// <summary>버전 목록(버전번호 오름차순). 없으면 빈 목록.</summary>
    Task<IReadOnlyList<ModelVersionManifest>> ListVersionsAsync(
        string scenarioId, CancellationToken ct = default);

    /// <summary>단일 버전 manifest. 없으면 null.</summary>
    Task<ModelVersionManifest?> ReadManifestAsync(
        string scenarioId, string version, CancellationToken ct = default);

    /// <summary>active 포인터. 미등록(포인터 파일 없음)이면 null.</summary>
    Task<ModelPointer?> ReadPointerAsync(string scenarioId, CancellationToken ct = default);

    /// <summary>지정 버전을 active 로 격상(previous ← 현 active). 버전 없으면 KeyNotFoundException.</summary>
    Task PromoteAsync(string scenarioId, string version, string by, CancellationToken ct = default);

    /// <summary>active↔previous 스왑(직전 promote 취소). previous 없으면 false.</summary>
    Task<bool> RollbackAsync(string scenarioId, string by, CancellationToken ct = default);
}
