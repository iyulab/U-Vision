using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 플라이휠 곡선 조회 — 사람 확정 라벨이 쌓이는 순서대로 «실제 판정» 과 «확정 사례 기억» 의 정확도를 구간마다 낸다. 무인증 읽기
/// (<c>/memory</c> 와 같은 정책 — 바꾸는 것이 없다). 장부와 라벨을 읽기만 하고, 읽는 법은 문턱 재생과 같다(<see cref="ReplaySource"/>).
/// </summary>
public static class FlywheelEndpoints
{
    /// <summary>구간 크기의 기본값과 상한 — 작을수록 점이 많고 흔들린다.</summary>
    public const int DefaultBucketSize = 25;
    public const int MaxBucketSize = 500;

    public static void MapFlywheelEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/flywheel", GetFlywheelAsync)
            .WithName("GetFlywheel")
            .Produces<FlywheelReport>()
            .ProducesProblem(400)
            .ProducesProblem(404);
    }

    private static async Task<IResult> GetFlywheelAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IScenarioStore scenarioStore,
        ILabelStore labelStore,
        IMetricsStore metricsStore,
        StoragePaths paths,
        MemoryOptions options,
        CancellationToken cancellationToken,
        [FromQuery(Name = "date")] string? date = null,
        [FromQuery(Name = "days")] int days = 30,
        [FromQuery(Name = "bucket")] int bucket = DefaultBucketSize)
    {
        date ??= DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (days is < 1 or > ReplayWindow.MaxDays)
            return Results.Problem(statusCode: 400, detail: $"days 는 1 이상 {ReplayWindow.MaxDays} 이하여야 합니다");
        if (bucket is < 1 or > MaxBucketSize)
            return Results.Problem(statusCode: 400, detail: $"bucket 은 1 이상 {MaxBucketSize} 이하여야 합니다");
        try
        {
            var scenario = await scenarioStore.GetAsync(scenarioId, cancellationToken);
            if (scenario is null)
                return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}");
            var effective = EffectiveScenario.Resolve(scenario, mlEnabled: false);
            var threshold = effective.MemoryMaxDistance ?? options.MaxDistance;

            var window = ReplayWindow.Dates(date, days);
            // 전용 ML 의 답은 장부 annotation 이 아니라 판정마다 한 줄 남는 메트릭 행에 있다 — 날마다 한 파일을 읽어 이미지 id 로 잇는다.
            var mlLabels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var day in window)
                foreach (var row in await metricsStore.ReadAsync(scenarioId, day, cancellationToken))
                    if (row.MlLabel is not null)
                        mlLabels[row.ImageId] = row.MlLabel;

            var cases = new List<CurveCase>();
            await foreach (var entry in ReplaySource.ReadAsync(scenarioId, window, labelStore, paths, cancellationToken))
            {
                mlLabels.TryGetValue(entry.SubjectId, out var ml);
                if (FlywheelCurve.FromAnnotations(entry.Annotations, effective, entry.HumanLabel, ml) is { } c)
                    cases.Add(c);
            }

            return Results.Ok(new FlywheelReport
            {
                ScenarioId = scenarioId,
                Date = date,
                Days = days,
                BucketSize = bucket,
                MemoryMaxDistance = threshold,
                Labeled = cases.Count,
                Points = [.. FlywheelCurve.Build(cases, effective, bucket, threshold).Select(Map)],
            });
        }
        catch (ArgumentException) // 형식 위반 scenario_id/date → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 date");
        }
    }

    private static FlywheelPoint Map(CurvePoint p) => new()
    {
        LabeledThrough = p.LabeledThrough,
        Judged = p.Judged,
        OperativeAgreement = p.OperativeAgreement,
        LabeledDefects = p.LabeledDefects,
        OperativeMissed = p.OperativeMissed,
        OperativeDefectPass = p.OperativeDefectPass,
        MemoryAnswered = p.MemoryAnswered,
        MemoryAnsweredRate = p.MemoryAnsweredRate,
        MemoryAgreement = p.MemoryAgreement,
        MemoryMissed = p.MemoryMissed,
        MlChecked = p.MlChecked,
        MlAgreement = p.MlAgreement,
        MlMissed = p.MlMissed,
    };
}
