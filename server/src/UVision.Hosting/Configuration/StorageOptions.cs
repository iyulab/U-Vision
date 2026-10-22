namespace UVision.Api.Configuration;

/// <summary>
/// 파일시스템 저장소 설정 — <c>appsettings.json</c> 의 <c>Storage</c> 섹션에서 바인딩한다.
/// (환경변수 override: <c>Storage__DataPath</c>)
///
/// 저장소는 <b>순수 파일시스템</b>(DB 미사용, ROADMAP 확정 전제). 시나리오 정의·기준 이미지·
/// 캡처·판정 결과가 <see cref="DataPath"/> 아래에 누적된다 — 단일조직 셀프호스트의 진실의 원천.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// 데이터 루트. 상대경로는 ContentRoot 기준으로 절대화된다(<see cref="Storage.StoragePaths"/>).
    /// </summary>
    public string DataPath { get; set; } = "data";

    /// <summary>
    /// 멱등 키(<c>Idempotency-Key</c>)의 재전송 응답을 남겨 두는 시간. 지나면 지워지고, 그 뒤 같은 키는 새 요청으로
    /// 판정된다. 장치는 응답을 잃으면 연결이 돌아오는 대로 다시 보내므로 며칠이면 넉넉하다. 0 이하는 지우지 않는다.
    /// </summary>
    public int IdempotencyRetentionHours { get; set; } = 168;
}
