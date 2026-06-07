using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 캡처 이미지 + 판정 결과의 영속화 저장소(system-of-record).
/// 이미지를 먼저 쓰고 <c>{image_id}.json</c> 으로 커밋한다 — json 존재 = 완결 레코드.
/// </summary>
public interface IInspectionStore
{
    /// <summary>
    /// 캡처 이미지와 판정 결과를 atomic 하게 저장한다. must-succeed —
    /// 실패 시 예외를 던진다(endpoint 에서 500, 200 위장 금지).
    /// </summary>
    Task SaveAsync(
        ReadOnlyMemory<byte> image,
        string ext,
        StoredResult result,
        CancellationToken cancellationToken = default);

    /// <summary>시나리오·날짜의 완결 레코드를 image_id 순으로 조회한다.</summary>
    Task<IReadOnlyList<StoredResult>> ListAsync(
        string scenarioId, string date, CancellationToken cancellationToken = default);
}
