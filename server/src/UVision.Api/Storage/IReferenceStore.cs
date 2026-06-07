using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>읽어 온 기준 이미지 바이트 + content-type(서빙용).</summary>
public sealed record ReferenceBytes(byte[] Data, string ContentType);

/// <summary>
/// 기준 이미지(OK/NG) 파일시스템 저장소 — <c>references/{ok|ng}/{refId}{ext}</c>.
/// NG 레이블 텍스트는 scenario.json(<see cref="IScenarioStore"/>)에 산다 — 여기는 바이트만 다룬다.
/// </summary>
public interface IReferenceStore
{
    /// <summary>기준 이미지를 저장하고 생성된 refId 를 반환한다(atomic).</summary>
    Task<string> SaveAsync(
        string scenarioId, ReferenceLabel label, ReadOnlyMemory<byte> image, string ext,
        CancellationToken cancellationToken = default);

    /// <summary>시나리오의 기준 이미지 목록(refId+label). NgLabel 은 호출부가 scenario 로 채운다.</summary>
    Task<IReadOnlyList<ReferenceInfo>> ListAsync(
        string scenarioId, CancellationToken cancellationToken = default);

    /// <summary>기준 이미지 바이트를 읽는다(서빙). 없으면 null.</summary>
    Task<ReferenceBytes?> ReadAsync(
        string scenarioId, ReferenceLabel label, string refId,
        CancellationToken cancellationToken = default);

    /// <summary>기준 이미지를 삭제한다. 존재해 삭제했으면 true.</summary>
    Task<bool> DeleteAsync(
        string scenarioId, ReferenceLabel label, string refId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// few-shot 결합용 이미지 로드. label 당 최대 <paramref name="maxPerLabel"/> 개(비용 상한).
    /// <paramref name="ngLabels"/>(scenario.json)로 NG 레이블을 결합한다.
    /// </summary>
    Task<IReadOnlyList<ReferenceImage>> LoadImagesAsync(
        string scenarioId, IReadOnlyDictionary<string, string> ngLabels, int maxPerLabel,
        CancellationToken cancellationToken = default);
}
