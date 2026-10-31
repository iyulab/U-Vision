using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 시나리오 CRUD — 관리자가 검사 유형을 코드 없이 구성한다(ROADMAP Phase 2 S-B).
/// 읽기(GET)는 운영 화면도 필요하므로 무인증, 변경(POST/PUT/DELETE)은 관리자 PIN 보호.
/// </summary>
public static class ScenarioEndpoints
{
    public static void MapScenarioEndpoints(this RouteGroupBuilder parent)
    {
        var group = parent.MapGroup("/scenarios");

        group.MapGet("", async (IScenarioStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)))
            .WithName("ListScenarios")
            .Produces<IReadOnlyList<Scenario>>();

        group.MapGet("/{id}", async (string id, IScenarioStore store, CancellationToken ct) =>
            await WithValidId(id, async () =>
            {
                var scenario = await store.GetAsync(id, ct);
                return scenario is null
                    ? Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}")
                    : Results.Ok(scenario);
            }))
            .WithName("GetScenario")
            .Produces<Scenario>()
            .ProducesProblem(400)
            .ProducesProblem(404);

        group.MapPost("", async (ScenarioInput input, IScenarioStore store, HttpRequest request, UVisionOptions options, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
                return Results.Problem(statusCode: 400, detail: "name 은 필수");
            if (EffectiveScenario.Validate(input) is { } invalid)
                return Results.Problem(statusCode: 400, detail: invalid);

            Scenario? created;
            try
            {
                created = await store.CreateAsync(input, ct);
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: 400,
                    detail: $"scenario_id 는 영문 · 숫자 · . _ - 만(128자 이하): {input.ScenarioId}");
            }
            return created is null
                ? Results.Problem(statusCode: 409, detail: $"이미 있는 시나리오 id: {input.ScenarioId}")
                : Results.Created(ApiLocation.For(request, options, $"scenarios/{created.ScenarioId}"), created);
        }).RequireAdminPin()
            .WithName("CreateScenario")
            .Produces<Scenario>(201)
            .ProducesProblem(400)
            .ProducesProblem(409);

        group.MapPut("/{id}",
            async (string id, ScenarioInput input, IScenarioStore store, CancellationToken ct) =>
            await WithValidId(id, async () =>
            {
                if (string.IsNullOrWhiteSpace(input.Name))
                    return Results.Problem(statusCode: 400, detail: "name 은 필수");
                if (input.ScenarioId is { } other && other != id)
                    return Results.Problem(statusCode: 400, detail: "시나리오 id 는 바꿀 수 없다");
                // 생략한 유형 · 계수 필드는 저장된 값이 보존되므로 합쳐진 모양으로 검증한다.
                if (EffectiveScenario.Validate(input, await store.GetAsync(id, ct)) is { } invalid)
                    return Results.Problem(statusCode: 400, detail: invalid);

                var updated = await store.UpdateAsync(id, input, ct);
                return updated is null
                    ? Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}")
                    : Results.Ok(updated);
            })).RequireAdminPin()
            .WithName("UpdateScenario")
            .Produces<Scenario>()
            .ProducesProblem(400)
            .ProducesProblem(404);

        group.MapDelete("/{id}", async (string id, IScenarioStore store, CancellationToken ct) =>
            await WithValidId(id, async () =>
            {
                var deleted = await store.DeleteAsync(id, ct);
                return deleted
                    ? Results.NoContent()
                    : Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}");
            })).RequireAdminPin()
            .WithName("DeleteScenario")
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(404);
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
