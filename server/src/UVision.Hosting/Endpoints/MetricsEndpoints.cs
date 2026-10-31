using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Metrics;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 메트릭 조회 엔드포인트(신뢰성 플라이휠 B3 read) — 시나리오·날짜의 예측 신호 + 사람 라벨을
/// 집계해 agreement rate·degrade율·검토율·NG recall(VLM·ML)을 돌려준다.
/// <para>
/// 무인증 읽기 — 운영 read 엔드포인트(<c>/results</c>) 정책과 일치(변경 아님 → PIN 불필요).
/// Phase 4 대시보드의 데이터 소스이자 Q2 평가·Q3 재빌드 트리거·A1 권한이양 격상의 근거.
/// </para>
/// </summary>
public static class MetricsEndpoints
{
    public static void MapMetricsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/metrics", GetMetricsAsync)
            .WithName("GetMetrics")
            .Produces<MetricsSummary>()
            .ProducesProblem(400);
        // 메트릭이 있는 날짜 — 검사 날짜 목록과 다르다: 검출기 불능은 레코드 없이 행만 남기고 보존 삭제는 부류별이라,
        // 레코드가 없는 날에도 읽을 메트릭이 있을 수 있다.
        group.MapGet("/metrics/dates", ListMetricsDatesAsync)
            .WithName("ListMetricsDates")
            .Produces<IReadOnlyList<string>>()
            .ProducesProblem(400);
    }

    private static async Task<IResult> ListMetricsDatesAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IMetricsStore metricsStore,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await metricsStore.ListDatesAsync(scenarioId, cancellationToken));
        }
        catch (ArgumentException) // 형식 위반 scenario_id → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id");
        }
    }

    private static async Task<IResult> GetMetricsAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IMetricsStore metricsStore,
        ILabelStore labelStore,
        IScenarioStore scenarioStore,
        AuthorityOptions authorityOptions,
        CancellationToken cancellationToken,
        [FromQuery(Name = "date")] string? date = null)
    {
        // date 생략 시 오늘(UTC) — /results 와 동일 규약.
        date ??= DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        try
        {
            var rows = await metricsStore.ReadAsync(scenarioId, date, cancellationToken);
            var labels = await labelStore.ListAsync(scenarioId, date, cancellationToken);
            // 삭제된 시나리오의 지난 메트릭은 이분 판정으로 읽는다.
            var scenario = await scenarioStore.GetAsync(scenarioId, cancellationToken);
            var isNg = scenario is null ? null : EffectiveScenario.Resolve(scenario, mlEnabled: false).NgLabelRule;
            // 메트릭 없음(ML 비활성·미검사)도 200 빈 집계 — "데이터 없음"을 404 가 아니라 0 으로 정직히.
            return Results.Ok(MetricsAggregator.Summarize(scenarioId, date, rows, labels, authorityOptions, isNg));
        }
        catch (ArgumentException) // 형식 위반 scenario_id/date → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 date");
        }
    }
}
