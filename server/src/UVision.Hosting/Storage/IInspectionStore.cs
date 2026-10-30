using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>읽어 온 검사 이미지 바이트 + content-type(서빙용). <see cref="ReferenceBytes"/> 와 동형.</summary>
public sealed record InspectionImage(byte[] Data, string ContentType);

/// <summary>결과 목록 한 쪽 — <see cref="NextCursor"/> 는 더 있을 때만 있다(다음 쪽을 부를 때 그대로 돌려준다).</summary>
public sealed record ResultPage(IReadOnlyList<StoredResult> Items, string? NextCursor);

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

    /// <summary>
    /// 시나리오·날짜의 완결 레코드를 <b>최신 먼저</b>(레코드 파일의 기록 시각) 한 쪽씩 조회한다 — 읽는 것은 그 쪽의 레코드뿐이다
    /// (네트워크 공유에서 레코드 하나가 파일 열기 한 번이라 «오늘 전부» 읽기가 느리다). <paramref name="cursor"/> 는 이전 쪽의
    /// <see cref="ResultPage.NextCursor"/> — 이후에 기록된 레코드는 커서 앞(더 최신)에 놓이므로 쪽 사이에 새 검사가 들어와도
    /// 밀리거나 겹치지 않는다. 형식 위반(<paramref name="limit"/> &lt; 1 · 읽을 수 없는 커서)은 <see cref="ArgumentException"/>.
    /// </summary>
    Task<ResultPage> ListPageAsync(
        string scenarioId, string date, int limit, string? cursor, CancellationToken cancellationToken = default);

    /// <summary>
    /// 저장된 캡처 이미지 바이트를 읽는다(조회 UI 서빙). stem(<paramref name="imageId"/>)으로
    /// 디렉토리에서 매칭(ext 무관, json 제외). 없으면 null.
    /// </summary>
    Task<InspectionImage?> ReadImageAsync(
        string scenarioId, string date, string imageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 시나리오에 검사 기록이 있는 날짜 목록(yyyy-MM-dd, 최신 먼저). 기록 없으면 빈 리스트.
    /// </summary>
    Task<IReadOnlyList<string>> ListDatesAsync(
        string scenarioId, CancellationToken cancellationToken = default);
}
