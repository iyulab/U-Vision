using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Dataset;
using UVision.Api.Services.Scenarios;
using UVision.Api.Services.Training;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 데이터셋 export 엔드포인트(신뢰성 플라이휠 ② — 전용 ML 빌드 데이터 준비).
/// 사람 라벨된 이미지를 MLoop 이미지분류 학습 입력 스냅샷으로 내보낸다.
/// <para>
/// 읽기(목록·manifest)는 무인증(운영 데이터 조회), <b>export 생성은 관리자 PIN</b>
/// (파생 산출물을 디스크에 쓰는 관리 작업).
/// </para>
/// </summary>
public static class DatasetEndpoints
{
    public static void MapDatasetEndpoints(this RouteGroupBuilder parent)
    {
        var group = parent.MapGroup("/datasets");

        group.MapGet("", ListAsync)
            .WithName("ListDatasetExports")
            .Produces<IReadOnlyList<string>>()
            .ProducesProblem(400);
        group.MapGet("/{exportId}/manifest", ManifestAsync)
            .WithName("GetDatasetManifest")
            .Produces<DatasetExportManifest>()
            .ProducesProblem(400)
            .ProducesProblem(404);
        group.MapGet("/readiness", ReadinessAsync)
            .WithName("GetTrainingReadiness")
            .Produces<TrainingReadinessResponse>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/export", ExportAsync).RequireAdminPin()
            .WithName("ExportDataset")
            .Produces<DatasetExportManifest>(201)
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
    }

    private static async Task<IResult> ListAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IDatasetExporter exporter,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await exporter.ListExportsAsync(scenarioId, ct));
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id");
        }
    }

    private static async Task<IResult> ManifestAsync(
        string exportId,
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IDatasetExporter exporter,
        CancellationToken ct)
    {
        try
        {
            var manifest = await exporter.ReadManifestAsync(scenarioId, exportId, ct);
            return manifest is null
                ? Results.Problem(statusCode: 404, detail: "export 없음")
                : Results.Ok(manifest);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 export_id");
        }
    }

    private static async Task<IResult> ExportAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IScenarioStore scenarioStore,
        IDatasetExporter exporter,
        DatasetExportTrimmer trimmer,
        HttpRequest request,
        UVisionOptions options,
        CancellationToken ct)
    {
        try
        {
            var scenario = await scenarioStore.GetAsync(scenarioId, ct);
            if (scenario is null)
                return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}");

            if (scenario.Type == ScenarioVocabulary.Counting)
                return Results.Problem(statusCode: 409, detail: NotTrainable);

            var exportId = NewExportId();
            var rules = EffectiveScenario.Resolve(scenario, mlEnabled: false).Training;
            var manifest = await exporter.ExportAsync(scenarioId, exportId, rules, ct);
            await trimmer.TrimAsync(scenarioId, exportId, ct); // 옵션(기본 꺼짐)이 켜졌을 때만 오래된 export 를 정리
            return Results.Created(
                ApiLocation.For(request, options, $"datasets/{exportId}/manifest?scenario_id={scenarioId}"), manifest);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id");
        }
    }

    /// <summary>
    /// 학습 준비 신호 — 데이터셋과 같은 선정 규칙으로 센다. 쓰는 모델이 배운 데이터셋은 모델 레지스트리의
    /// <c>export_id</c> 로 찾는다. ⚠️ UNVERIFIED: 실 운영 라벨에서 기준값(클래스당 · 재빌드 문턱)이 맞는지는 미측정.
    /// </summary>
    private static async Task<IResult> ReadinessAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IScenarioStore scenarioStore,
        IInspectionStore inspections,
        ILabelStore labels,
        IModelRegistry registry,
        IDatasetExporter exporter,
        CancellationToken ct)
    {
        try
        {
            var scenario = await scenarioStore.GetAsync(scenarioId, ct);
            if (scenario is null)
                return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}");

            if (scenario.Type == ScenarioVocabulary.Counting)
                return Results.Problem(statusCode: 409, detail: NotTrainable);

            var effective = EffectiveScenario.Resolve(scenario, mlEnabled: false);
            var candidates = await new TrainingCandidateSource(inspections, labels).LoadAsync(scenarioId, ct);
            var selection = TrainingSetSelector.Select(candidates.Items, effective.Training);

            var active = (await registry.ReadPointerAsync(scenarioId, ct))?.ActiveVersion;
            string? trainedAt = null;
            if (active is not null
                && await registry.ReadManifestAsync(scenarioId, active, ct) is { ExportId: { } exportId })
                trainedAt = (await exporter.ReadManifestAsync(scenarioId, exportId, ct))?.CreatedAt;

            var r = TrainingReadiness.Evaluate(effective.Labels, selection.Selected, effective.Training, active, trainedAt);
            return Results.Ok(new TrainingReadinessResponse
            {
                ScenarioId = scenarioId,
                Classes = r.Classes.Select(c => new TrainingClassCountDto { Id = c.Id, Count = c.Count }).ToList(),
                MinPerClass = r.MinPerClass,
                BuildReady = r.BuildReady,
                ActiveVersion = r.ActiveVersion,
                NewLabelsSinceModel = r.NewLabelsSinceModel,
                RebuildMinNewLabels = effective.Training.RebuildMinNewLabels,
                RebuildSuggested = r.RebuildSuggested,
            });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id");
        }
    }

    // 계수의 OK/NG 는 «개수가 맞는가» 이지 사진의 불량이 아니다 — 이미지 분류 학습 입력으로 내보내면 엉뚱한 분류기를 가르친다.
    private const string NotTrainable =
        "계수 시나리오는 분류 학습 데이터셋을 만들지 않는다(OK/NG 는 개수가 허용 범위 안이냐이지 사진의 불량이 아니다)";

    /// <summary>타임스탬프 기반 export id(정렬 가능 + 충돌 방지 짧은 suffix). Id() 허용 문자만.</summary>
    private static string NewExportId() =>
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("N")[..6];
}
