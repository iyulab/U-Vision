using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 관측성 메트릭의 append-only 시계열 저장소(신뢰성 플라이휠 B3).
/// <c>{scenario}/metrics/{yyyy-MM-dd}.jsonl</c> — DB 없이(헌법 non-goal) 파일시스템 위 시계열.
/// <para>
/// system-of-record(<see cref="IInspectionStore"/>)와 구분된다 — 메트릭은 <b>관측</b>이지 진실의 원천이
/// 아니다. 따라서 쓰기 실패는 degrade(판정·응답 무차단)이고, 손상된 줄은 읽기에서 skip 한다.
/// </para>
/// </summary>
public interface IMetricsStore
{
    /// <summary>
    /// 메트릭 row 를 시나리오의 날짜 버킷 jsonl 에 append 한다(날짜는 <see cref="MetricsRow.Timestamp"/>
    /// 의 UTC 날짜로 도출 — 검사 결과와 같은 날짜 버킷). 프로세스 내 append 는 직렬화되어 부분쓰기를
    /// 방지한다. 실패는 호출자가 degrade 로 처리한다(must-succeed 아님).
    /// </summary>
    Task AppendAsync(string scenarioId, MetricsRow row, CancellationToken cancellationToken = default);

    /// <summary>
    /// 시나리오·날짜의 메트릭 row 를 기록 순서대로 읽는다(집계의 입력). 빈 줄·파싱 실패 줄은 skip 한다
    /// (append 도중 크래시 내성). 파일 없으면 빈 리스트.
    /// </summary>
    Task<IReadOnlyList<MetricsRow>> ReadAsync(
        string scenarioId, string date, CancellationToken cancellationToken = default);
}
