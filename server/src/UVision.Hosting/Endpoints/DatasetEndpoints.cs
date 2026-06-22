using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Auth;
using UVision.Api.Services.Dataset;
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

        group.MapGet("", ListAsync);
        group.MapGet("/{exportId}/manifest", ManifestAsync);
        group.MapPost("/export", ExportAsync).RequireAdminPin();
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
        CancellationToken ct)
    {
        try
        {
            var scenario = await scenarioStore.GetAsync(scenarioId, ct);
            if (scenario is null)
                return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}");

            var exportId = NewExportId();
            var manifest = await exporter.ExportAsync(scenarioId, exportId, ct);
            return Results.Created(
                $"/datasets/{exportId}/manifest?scenario_id={scenarioId}", manifest);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id");
        }
    }

    /// <summary>타임스탬프 기반 export id(정렬 가능 + 충돌 방지 짧은 suffix). Id() 허용 문자만.</summary>
    private static string NewExportId() =>
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("N")[..6];
}
