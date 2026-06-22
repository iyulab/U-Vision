using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 사람 라벨 사이드카 저장소 — 불변 VLM 레코드와 분리된 가변 주석.
/// 결과의 날짜 버킷(<paramref name="date"/>) 아래 <c>{image_id}.label.json</c>.
/// </summary>
public interface ILabelStore
{
    /// <summary>라벨 사이드카를 atomic 하게 쓴다(정정 = 덮어쓰기, last-write-wins).</summary>
    Task WriteAsync(
        string scenarioId, string date, StoredLabel label,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// operative 라벨(또는 해소)을 append 한다 — read-modify-append(이력 누적, 덮어쓰지 않음).
    /// 직전 audit 상태가 conflicted 면 이 쓰기가 resolved 로 전이시킨다(C1). <paramref name="by"/>=device id.
    /// </summary>
    Task AppendLabelAsync(
        string scenarioId, string date, string imageId, string label, string by,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 블라인드 재라벨을 append 하고 감사 상태를 계산한다(C1) — operative 라벨은 불변(측정).
    /// 라벨 없는 이미지면 <see cref="InvalidOperationException"/>. 반환=상태 + 공개된 직전 operative 라벨.
    /// </summary>
    Task<AuditOutcome> AppendAuditAsync(
        string scenarioId, string date, string imageId, string auditLabel, string by,
        CancellationToken cancellationToken = default);

    /// <summary>라벨 사이드카를 읽는다. 없으면 null(미라벨).</summary>
    Task<StoredLabel?> ReadAsync(
        string scenarioId, string date, string imageId,
        CancellationToken cancellationToken = default);

    /// <summary>라벨 사이드카를 삭제한다(미라벨로 환원). 없으면 no-op.</summary>
    Task DeleteAsync(
        string scenarioId, string date, string imageId,
        CancellationToken cancellationToken = default);

    /// <summary>시나리오·날짜의 모든 라벨(표 병합용). 없으면 빈 리스트.</summary>
    Task<IReadOnlyList<StoredLabel>> ListAsync(
        string scenarioId, string date, CancellationToken cancellationToken = default);
}

/// <summary>블라인드 재라벨 결과 — 감사 상태 + 공개된 직전 operative 라벨(C1).</summary>
public sealed record AuditOutcome
{
    public required string Status { get; init; }
    public required string PriorLabel { get; init; }
}
