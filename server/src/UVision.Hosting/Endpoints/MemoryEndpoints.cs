using System.Globalization;
using HoneAI;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 확정 사례 기억 조회 — 기억의 상태(판 · 사례 수 · 지은 시각)와 그날 장부로 돌린 문턱 재생. 무인증 읽기(<c>/metrics</c> 와
/// 같은 정책 — 바꾸는 것이 없다). 기억이 꺼져 있어도 200 으로 <c>enabled: false</c> 와 빈 재생을 돌려준다.
/// </summary>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/memory", GetMemoryAsync)
            .WithName("GetMemory")
            .Produces<MemoryReport>()
            .ProducesProblem(400)
            .ProducesProblem(404);
    }

    private static async Task<IResult> GetMemoryAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IScenarioStore scenarioStore,
        ILabelStore labelStore,
        CaseMemory memory,
        StoragePaths paths,
        MemoryOptions options,
        CancellationToken cancellationToken,
        [FromQuery(Name = "date")] string? date = null,
        [FromQuery(Name = "days")] int days = 1)
    {
        date ??= DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (days is < 1 or > ReplayWindow.MaxDays)
            return Results.Problem(statusCode: 400, detail: $"days 는 1 이상 {ReplayWindow.MaxDays} 이하여야 합니다");
        try
        {
            var scenario = await scenarioStore.GetAsync(scenarioId, cancellationToken);
            if (scenario is null)
                return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}");
            var effective = EffectiveScenario.Resolve(scenario, mlEnabled: false, memoryEnabled: memory.IsEnabled);
            var enabled = memory.IsEnabled && effective.Tiers.Contains(ScenarioVocabulary.MemoryTier);

            var cases = new List<ReplayCase>();
            var neighborRecords = new List<NeighborRecord>();
            // 하루면 지금 동작 그대로 — 여러 날이면 마지막 날(date)부터 거슬러 같은 읽기를 날마다 한다(라벨은 그날의 기록에 붙는다).
            await foreach (var entry in ReplaySource.ReadAsync(
                scenarioId, days == 1 ? new[] { date } : ReplayWindow.Dates(date, days), labelStore, paths, cancellationToken))
            {
                if (MemoryReplay.FromAnnotations(entry.Annotations, effective, entry.HumanLabel) is not { } c) continue;
                cases.Add(c);
                // 이웃 순서를 남긴 줄만 k 곡선의 재료다(기록 경로가 생기기 전의 줄은 다시 셀 수 없다).
                if (entry.Annotations is { } annotations && annotations.ContainsKey(MemoryAnswer.NeighborClassesKey))
                    neighborRecords.Add(new NeighborRecord(c, annotations));
            }

            var configuredDistance = effective.MemoryMaxDistance ?? options.MaxDistance;
            var replay = cases.Count == 0 ? [] : MemoryReplay.Run(cases, effective, configuredDistance);
            // 목표 놓침률 0 은 어떤 증거로도 주장할 수 없다 — 시나리오가 0 을 골랐으면 기본 목표로 센다.
            var target = effective.Targets.MaxMissRate > 0 ? effective.Targets.MaxMissRate : ScenarioTargets.Default.MaxMissRate;

            var status = memory.StatusOf(scenarioId);
            return Results.Ok(new MemoryReport
            {
                ScenarioId = scenarioId,
                Date = date,
                Enabled = enabled,
                Encoder = status?.EncoderKey,
                Cases = status?.Cases,
                BuiltAt = status?.BuiltAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                Replayed = cases.Count,
                Replay = replay,
                Days = days == 1 ? null : days,
                Recommendation = replay.Count == 0 ? null : MemoryRecommendation.From(
                    OptionAdvisor.AdviseMaxDistance(replay, target), target,
                    NeighborCurve.Compute(neighborRecords, effective, configuredDistance, options.Neighbors)),
            });
        }
        catch (ArgumentException) // 형식 위반 scenario_id/date → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 date");
        }
    }
}
