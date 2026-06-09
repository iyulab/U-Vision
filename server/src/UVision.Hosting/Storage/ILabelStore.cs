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
