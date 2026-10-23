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
        [FromQuery(Name = "date")] string? date = null)
    {
        date ??= DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            var scenario = await scenarioStore.GetAsync(scenarioId, cancellationToken);
            if (scenario is null)
                return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}");
            var effective = EffectiveScenario.Resolve(scenario, mlEnabled: false, memoryEnabled: memory.IsEnabled);
            var enabled = memory.IsEnabled && effective.Tiers.Contains(ScenarioVocabulary.MemoryTier);

            var labels = (await labelStore.ListAsync(scenarioId, date, cancellationToken))
                .Where(l => l.OperativeLabel is not null)
                .ToDictionary(l => l.ImageId, l => l.OperativeLabel!, StringComparer.Ordinal);
            var cases = new List<ReplayCase>();
            var ledger = paths.LedgerJsonl(scenarioId, date);
            if (File.Exists(ledger))
            {
                await foreach (var record in new JsonlProvenanceSink(ledger).ReadAsync(cancellationToken: cancellationToken))
                {
                    labels.TryGetValue(record.SubjectId, out var label);
                    if (MemoryReplay.FromAnnotations(record.Provenance.Annotations, effective, label) is { } c)
                        cases.Add(c);
                }
            }

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
                Replay = cases.Count == 0 ? [] : MemoryReplay.Run(cases, effective, options.MaxDistance),
            });
        }
        catch (ArgumentException) // 형식 위반 scenario_id/date → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 date");
        }
    }
}
