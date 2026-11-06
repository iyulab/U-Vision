using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Memory;
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
        [FromQuery(Name = "date")] string? date = null,
        [FromQuery(Name = "days")] int days = 1)
    {
        if (days is < 1 or > ReplayWindow.MaxDays)
            return Results.Problem(statusCode: 400, detail: $"days 는 1 이상 {ReplayWindow.MaxDays} 이하여야 합니다");

        try
        {
            // days=1(기본) 은 날짜 하나 — 응답 바이트 그대로. 2 이상이면 date 가 마지막 날인 창 전체의 행 · 라벨을 한 집계로 센다.
            // 메트릭 없음(ML 비활성·미검사)도 200 빈 집계 — "데이터 없음"을 404 가 아니라 0 으로 정직히.
            return Results.Ok(await MetricsWindow.SummarizeAsync(
                scenarioId, date, days, metricsStore, labelStore, scenarioStore, authorityOptions, cancellationToken));
        }
        catch (ArgumentException) // 형식 위반 scenario_id/date → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 date");
        }
    }
}
