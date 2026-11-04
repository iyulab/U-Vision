using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Services.Scenarios;

/// <summary>
/// 사진 판정 시나리오에만 있는 표면(데이터셋 · 기준 이미지 · 모델 버전 · 권한 사다리 · 사람 라벨)을 스트림 감시(<c>monitoring</c>)에서 닫는다.
/// 스트림 감시는 사진을 판정하지 않으므로 이 표면에 쓸 것이 없다 — 받아 주면 비어 있는 시나리오에 의미 없는 상태(빈 데이터셋 내보내기 ·
/// 쓰이지 않는 기준 이미지 · 사다리 이력)가 쌓인다. 읽기는 «아직 없음» 이라 빈 결과가 맞아 열어 둔다(대시보드가 시나리오를 한꺼번에 돈다).
/// 계수 시나리오의 데이터셋 내보내기가 409 인 것과 같은 관례(요청 모양이 아니라 시나리오의 유형과 맞지 않는다).
/// </summary>
public static class PhotoSurface
{
    public static string MonitoringDetail(string what) =>
        $"monitoring 시나리오는 사진을 판정하지 않는다 — {what} 은(는) 사진 판정 시나리오에만 있다(엣지 노드가 규칙으로 사건을 낸다)";

    /// <summary>이미 읽은 시나리오가 monitoring 이면 409, 아니면 null.</summary>
    public static IResult? RejectMonitoring(Scenario? scenario, string what) =>
        scenario?.Type == ScenarioVocabulary.Monitoring ? Results.Problem(statusCode: 409, detail: MonitoringDetail(what)) : null;

    /// <summary>
    /// 시나리오를 읽어 monitoring 이면 409. 없는 시나리오 · 형식이 틀린 id 는 여기서 판정하지 않는다(null) — 각 핸들러가 원래 하던 대로
    /// 400/404 를 낸다.
    /// </summary>
    public static async Task<IResult?> RejectMonitoringAsync(IScenarioStore store, string scenarioId, string what, CancellationToken ct)
    {
        Scenario? scenario;
        try { scenario = await store.GetAsync(scenarioId, ct); }
        catch (ArgumentException) { return null; }
        return RejectMonitoring(scenario, what);
    }
}
