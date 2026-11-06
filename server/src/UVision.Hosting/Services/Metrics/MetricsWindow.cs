using System.Globalization;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;

namespace UVision.Api.Services.Metrics;

/// <summary>
/// 메트릭 집계 창을 저장소에서 읽어 요약으로 만든다 — <c>GET /metrics</c> 와 격상 요청의 신호 확인이 같은 읽기를 쓴다
/// (콘솔이 보인 «격상 근거» 와 서버가 격상 때 본 신호가 어긋나지 않게).
/// </summary>
public static class MetricsWindow
{
    /// <summary><paramref name="days"/> 가 1 이면 날짜 하나(<c>window_days</c> 없는 요약), 2 이상이면 <paramref name="date"/> 가 마지막 날인 창 전체.</summary>
    /// <exception cref="ArgumentException">시나리오 id 나 날짜가 형식 위반.</exception>
    public static async Task<MetricsSummary> SummarizeAsync(
        string scenarioId, string? date, int days,
        IMetricsStore metricsStore, ILabelStore labelStore, IScenarioStore scenarioStore, AuthorityOptions authorityOptions,
        CancellationToken cancellationToken)
    {
        // date 생략 시 오늘(UTC) — /results 와 동일 규약.
        date ??= DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        foreach (var day in ReplayWindow.Dates(date, days))
        {
            rows.AddRange(await metricsStore.ReadAsync(scenarioId, day, cancellationToken));
            labels.AddRange(await labelStore.ListAsync(scenarioId, day, cancellationToken));
        }
        // 삭제된 시나리오의 지난 메트릭은 이분 판정으로 읽는다.
        var scenario = await scenarioStore.GetAsync(scenarioId, cancellationToken);
        var isNg = scenario is null ? null : EffectiveScenario.Resolve(scenario, mlEnabled: false).NgLabelRule;
        // 메트릭 없음(ML 비활성·미검사)도 빈 집계 — "데이터 없음"을 오류가 아니라 0 으로 정직히.
        return MetricsAggregator.Summarize(scenarioId, date, rows, labels, authorityOptions, isNg, days > 1 ? days : null);
    }
}
