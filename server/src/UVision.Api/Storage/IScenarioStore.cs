using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 시나리오 정의 저장소. S-A 는 읽기만(하드코딩 <c>demo</c> 대체) — CRUD 는 S-B 에서 확장한다.
/// </summary>
public interface IScenarioStore
{
    /// <summary>
    /// id 로 시나리오를 읽는다. 없으면 <c>null</c>(→404). id 형식 위반은
    /// <see cref="ArgumentException"/>(→400) — "존재하지 않음"과 "유효하지 않은 입력"을 구분한다.
    /// </summary>
    Task<Scenario?> GetAsync(string scenarioId, CancellationToken cancellationToken = default);

    /// <summary>모든 시나리오 정의를 읽는다.</summary>
    Task<IReadOnlyList<Scenario>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 새 시나리오를 만든다. id 는 <paramref name="input"/>.Name 에서 slug 로 도출하고
    /// 충돌 시 접미(<c>-2</c>, <c>-3</c> …)한다. 확정 id 를 담은 시나리오를 반환한다.
    /// </summary>
    Task<Scenario> CreateAsync(ScenarioInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// 기존 시나리오를 갱신한다(id 불변). 없으면 <c>null</c>(→404),
    /// id 형식 위반은 <see cref="ArgumentException"/>(→400).
    /// </summary>
    Task<Scenario?> UpdateAsync(
        string scenarioId, ScenarioInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// 시나리오와 그 하위 전체(기준이미지·검사 결과 포함)를 삭제한다.
    /// 존재해 삭제했으면 <c>true</c>, 없었으면 <c>false</c>(→404).
    /// </summary>
    Task<bool> DeleteAsync(string scenarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// NG 기준 이미지 레이블을 설정/제거한다(<paramref name="label"/> null 이면 제거 — 삭제 시 orphan 방지).
    /// 시나리오가 없으면 noop. 부분 갱신이므로 나머지 필드는 보존한다.
    /// </summary>
    Task SetNgLabelAsync(
        string scenarioId, string refId, string? label, CancellationToken cancellationToken = default);
}
