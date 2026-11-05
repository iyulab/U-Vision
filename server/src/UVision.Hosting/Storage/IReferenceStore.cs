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

    // --- 선별(classification) — 기준 이미지는 OK/NG 가 아니라 클래스 폴더(references/{class})에 둔다. 열거(ReferenceLabel)는 동결이라 건드리지 않고
    //     문자열 클래스 id 로 옆에 더했다. 검사 시나리오의 경로는 위 메서드 그대로다.

    /// <summary>클래스 하나의 기준 이미지를 저장한다 — <paramref name="classId"/> 는 시나리오가 선언한 클래스 id(경계에서 확인한다).</summary>
    Task<string> SaveClassAsync(
        string scenarioId, string classId, ReadOnlyMemory<byte> image, string ext,
        CancellationToken cancellationToken = default);

    /// <summary>선별 시나리오의 모든 클래스 폴더의 기준 이미지 — 선언을 벗어난(지워진 클래스의) 폴더도 그대로 보인다.</summary>
    Task<IReadOnlyList<ClassReference>> ListClassesAsync(
        string scenarioId, CancellationToken cancellationToken = default);

    Task<ReferenceBytes?> ReadClassAsync(
        string scenarioId, string classId, string refId, CancellationToken cancellationToken = default);

    Task<bool> DeleteClassAsync(
        string scenarioId, string classId, string refId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 선별의 few-shot 입력 — 선언한 클래스마다 최대 <paramref name="maxPerClass"/> 장(경로 순). 기준 이미지가 없는 클래스는 글 설명만으로
    /// 시작한다(건너뛴다). 각 이미지의 <see cref="ReferenceImage.ClassId"/> 가 프롬프트 표지가 된다.
    /// </summary>
    Task<IReadOnlyList<ReferenceImage>> LoadClassImagesAsync(
        string scenarioId, IReadOnlyList<ScenarioClass> classes, int maxPerClass,
        CancellationToken cancellationToken = default);
}
