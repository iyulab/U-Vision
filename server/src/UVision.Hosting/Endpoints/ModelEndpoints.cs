using Microsoft.AspNetCore.Mvc;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 모델 버전 레지스트리 엔드포인트(신뢰성 플라이휠 B1). 조회는 무인증(운영 대시보드), 변경(등록·격상·
/// 롤백)은 관리자 PIN(scenario CUD 대칭). 모델 바이너리는 MLoop 소유 — 여기서는 참조·이력만.
/// </summary>
public static class ModelEndpoints
{
    public static void MapModelEndpoints(this RouteGroupBuilder parent)
    {
        var group = parent.MapGroup("/scenarios/{id}/models");

        group.MapGet("", ListAsync);
        group.MapPost("", RegisterAsync).RequireAdminPin();
        group.MapPost("/{version}/promote", PromoteAsync).RequireAdminPin();
        group.MapPost("/rollback", RollbackAsync).RequireAdminPin();
    }

    private static async Task<IResult> ListAsync(
        string id, IModelRegistry registry, CancellationToken ct)
    {
        try
        {
            var versions = await registry.ListVersionsAsync(id, ct);
            var pointer = await registry.ReadPointerAsync(id, ct);
            return Results.Ok(new
            {
                active_version = pointer?.ActiveVersion,
                previous_version = pointer?.PreviousVersion,
                versions,
            });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
    }

    private static async Task<IResult> RegisterAsync(
        string id, RegisterModelInput input, IModelRegistry registry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.ModelName))
            return Results.Problem(statusCode: 400, detail: "model_name 필수");
        try
        {
            var version = await registry.RegisterAsync(id, new ModelRegistration
            {
                ModelName = input.ModelName,
                ExportId = input.ExportId,
                Endpoint = input.Endpoint,
                Metrics = input.Metrics,
                Note = input.Note,
                By = input.By ?? "",
            }, ct);
            return Results.Created($"/api/u-vision/scenarios/{id}/models/{version}", new { version });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 id/model_name/export_id");
        }
    }

    private static async Task<IResult> PromoteAsync(
        string id, string version, IModelRegistry registry, CancellationToken ct,
        [FromQuery(Name = "by")] string? by = null)
    {
        try
        {
            await registry.PromoteAsync(id, version, by ?? "", ct);
        }
        catch (KeyNotFoundException)
        {
            return Results.Problem(statusCode: 404, detail: $"모델 버전 없음: {version}");
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 id/version");
        }
        var pointer = await registry.ReadPointerAsync(id, ct);
        return Results.Ok(new { active_version = pointer!.ActiveVersion, previous_version = pointer.PreviousVersion });
    }

    private static async Task<IResult> RollbackAsync(
        string id, IModelRegistry registry, CancellationToken ct,
        [FromQuery(Name = "by")] string? by = null)
    {
        bool ok;
        try
        {
            ok = await registry.RollbackAsync(id, by ?? "", ct);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
        if (!ok)
            return Results.Problem(statusCode: 409, detail: "되돌릴 이전 버전이 없습니다.");
        var pointer = await registry.ReadPointerAsync(id, ct);
        return Results.Ok(new { active_version = pointer!.ActiveVersion, previous_version = pointer.PreviousVersion });
    }
}
