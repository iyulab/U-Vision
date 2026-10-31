using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Retention;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 보존 삭제 엔드포인트 — 설치자가 구간과 부류를 이름으로 골라 정본을 지운다. <b>관리자 PIN</b> 필수.
/// 엔진은 아무것도 스스로 지우지 않는다: 이 호출이 유일한 경로이고, 기본은 계획만(<c>dry_run</c>)이다.
/// </summary>
public static class RetentionEndpoints
{
    public static void MapRetentionEndpoints(this RouteGroupBuilder parent)
    {
        parent.MapPost("/scenarios/{id}/retention/purge", PurgeAsync).RequireAdminPin()
            .WithName("PurgeRetention")
            .Produces<RetentionPurgeResponse>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
    }

    private static async Task<IResult> PurgeAsync(
        string id, RetentionPurgeInput input, IScenarioStore scenarios, RetentionService retention, CancellationToken ct)
    {
        try
        {
            if (await scenarios.GetAsync(id, ct) is null)
                return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}");
            return Results.Ok(await retention.RunAsync(id, input, ct));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(statusCode: 400, detail: ex.Message);
        }
        catch (RetentionConflictException ex)
        {
            return Results.Problem(statusCode: 409, detail: ex.Message);
        }
    }
}
