using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 시나리오 CRUD — 관리자가 검사 유형을 코드 없이 구성한다(ROADMAP Phase 2 S-B).
/// 읽기(GET)는 운영 화면도 필요하므로 무인증, 변경(POST/PUT/DELETE)은 관리자 PIN 보호.
/// </summary>
public static class ScenarioEndpoints
{
    public static void MapScenarioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scenarios");

        group.MapGet("", async (IScenarioStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        group.MapGet("/{id}", async (string id, IScenarioStore store, CancellationToken ct) =>
            await WithValidId(id, async () =>
            {
                var scenario = await store.GetAsync(id, ct);
                return scenario is null
                    ? Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}")
                    : Results.Ok(scenario);
            }));

        group.MapPost("", async (ScenarioInput input, IScenarioStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.Problem(statusCode: 400, detail: "name 은 필수");

            var created = await store.CreateAsync(input, ct);
            return Results.Created($"/api/scenarios/{created.ScenarioId}", created);
        }).RequireAdminPin();

        group.MapPut("/{id}",
            async (string id, ScenarioInput input, IScenarioStore store, CancellationToken ct) =>
            await WithValidId(id, async () =>
            {
                if (string.IsNullOrWhiteSpace(input.Name))
                    return Results.Problem(statusCode: 400, detail: "name 은 필수");

                var updated = await store.UpdateAsync(id, input, ct);
                return updated is null
                    ? Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}")
                    : Results.Ok(updated);
            })).RequireAdminPin();

        group.MapDelete("/{id}", async (string id, IScenarioStore store, CancellationToken ct) =>
            await WithValidId(id, async () =>
            {
                var deleted = await store.DeleteAsync(id, ct);
                return deleted
                    ? Results.NoContent()
                    : Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}");
            })).RequireAdminPin();
    }

    /// <summary>id 형식 위반(<see cref="ArgumentException"/>)을 400 으로 매핑 — 404 와 구분.</summary>
    private static async Task<IResult> WithValidId(string id, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
    }
}
