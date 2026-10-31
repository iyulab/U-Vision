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

    /// <summary>
    /// 데이터 볼륨의 여유 공간이 이 값(바이트) 미만이면 <c>GET /health</c> 의 <c>data_volume.low</c> 가 true 가 된다 — 설치자가
    /// 디스크가 가득 차기 전에 알아채게 하는 신호다. 엔진은 이 값으로 아무것도 지우거나 거부하지 않는다(보존은 설치자가 정한다).
    /// 기본 2 GiB. 0 이하는 경고 없음(여유 수치만 보인다).
    /// </summary>
    public long LowSpaceWarningBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 데이터셋 export 를 새로 만든 뒤 시나리오마다 최근 이 개수만 남긴다(파생물 정리 — export 는 라벨에서 다시 만들 수 있다). 방금 만든 것과
    /// 모델 레지스트리가 <c>export_id</c> 로 가리키는 것은 이 수와 무관하게 남는다. 0 이하는 지우지 않는다(기본). 정본(판정 레코드 · 장부 ·
    /// 메트릭)은 이 옵션이 지우지 않는다 — 그건 <c>POST /scenarios/{id}/retention/purge</c> 가 설치자의 명시 행위로만 지운다.
    /// </summary>
    public int DatasetExportKeep { get; set; }
}
