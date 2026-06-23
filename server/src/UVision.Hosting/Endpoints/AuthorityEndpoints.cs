using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 권한 이양 사다리 엔드포인트(A1). 조회는 무인증(운영 대시보드), 변경(격상·격하)은 관리자 PIN.
/// 격상=단조 상향·수동 / 격하=1단계 하향(안전쪽). 자동격하는 inspect 인밴드(엔드포인트 아님).
/// </summary>
public static class AuthorityEndpoints
{
    public static void MapAuthorityEndpoints(this RouteGroupBuilder parent)
    {
        var group = parent.MapGroup("/scenarios/{id}/authority");
        group.MapGet("", GetAsync);
        group.MapPost("/promote", PromoteAsync).RequireAdminPin();
        group.MapPost("/demote", DemoteAsync).RequireAdminPin();
    }

    private static async Task<IResult> GetAsync(string id, IAuthorityStore store, CancellationToken ct)
    {
        try
        {
            var state = await store.ReadAsync(id, ct);
            // 부재 = advisory(기본) — 합성 상태로 응답(정직: history 빈 배열).
            return Results.Ok(state ?? new AuthorityState
            {
                Stage = AuthorityStage.Advisory, UpdatedAt = "", UpdatedBy = "",
            });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
    }

    private static async Task<IResult> PromoteAsync(
        string id, PromoteAuthorityInput input, IAuthorityStore store, CancellationToken ct)
    {
        try
        {
            var current = (await store.ReadAsync(id, ct))?.Stage ?? AuthorityStage.Advisory;
            if ((int)input.Stage <= (int)current)
                return Results.Problem(statusCode: 400,
                    detail: $"격상은 단조 상향만 — 현재 {current}, 요청 {input.Stage}");
            await store.SetStageAsync(id, input.Stage, input.By ?? "", "promote", input.Reason, ct);
            return Results.Ok(new { stage = input.Stage, previous_stage = current });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
    }

    private static async Task<IResult> DemoteAsync(
        string id, DemoteAuthorityInput input, IAuthorityStore store, CancellationToken ct)
    {
        try
        {
            var current = (await store.ReadAsync(id, ct))?.Stage ?? AuthorityStage.Advisory;
            if (current == AuthorityStage.Shadow)
                return Results.Problem(statusCode: 409, detail: "이미 최저 단계(shadow)입니다.");
            var target = (AuthorityStage)((int)current - 1);
            await store.SetStageAsync(id, target, input.By ?? "", "demote-manual", input.Reason, ct);
            return Results.Ok(new { stage = target, previous_stage = current });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
    }
}
