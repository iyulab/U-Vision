using UVision.Api.Auth;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Authority;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Metrics;
using UVision.Api.Services.Scenarios;
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
        group.MapGet("", GetAsync)
            .WithName("GetAuthority")
            .Produces<AuthorityState>()
            .ProducesProblem(400);
        group.MapPost("/promote", PromoteAsync).RequireAdminPin()
            .WithName("PromoteAuthority")
            .Produces<AuthorityTransitionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapPost("/demote", DemoteAsync).RequireAdminPin()
            .WithName("DemoteAuthority")
            .Produces<AuthorityTransitionResponse>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
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
        string id, PromoteAuthorityInput input, IAuthorityStore store, IScenarioStore scenarioStore,
        IMetricsStore metricsStore, ILabelStore labelStore, AuthorityOptions authorityOptions, CancellationToken ct)
    {
        if (await PhotoSurface.RejectMissingOrMonitoringAsync(scenarioStore, id, "권한 사다리", ct) is { } rejected)
            return rejected;
        try
        {
            var current = (await store.ReadAsync(id, ct))?.Stage ?? AuthorityStage.Advisory;
            if ((int)input.Stage <= (int)current)
                return Results.Problem(statusCode: 400,
                    detail: $"격상은 단조 상향만 — 현재 {current}, 요청 {input.Stage}");
            // 근거 없는 격상(D-116) — 신호가 «적격» 이 아닌데 ML 이 판정 권한을 받거나 단계를 건너뛰면 명시 확인이 있어야 한다.
            // 신호는 콘솔 «격상 근거» 와 같은 창 집계를 서버가 직접 읽는다(클라이언트가 준 값을 믿지 않는다). 읽는 일은 ML 이 판정 권한을
            // 받는 격상에만 한다 — 건너뛰기 판정은 신호가 필요 없다.
            bool? eligible = null;
            if (AuthorityPromotionGuard.GivesMlAuthority(input.Stage))
            {
                var summary = await MetricsWindow.SummarizeAsync(
                    id, null, Math.Clamp(authorityOptions.EvidenceWindowDays, 1, ReplayWindow.MaxDays), metricsStore, labelStore, scenarioStore, authorityOptions, ct);
                eligible = summary.PromotionEligible;
            }
            var basis = AuthorityPromotionGuard.UnverifiedBasis(current, input.Stage, eligible);
            if (basis.Count > 0 && input.AcknowledgeUnverified != true)
                return Results.Problem(statusCode: 409, detail: AuthorityPromotionGuard.Explain(basis));

            await store.SetStageAsync(
                id, input.Stage, input.By ?? "", "promote", input.Reason, ct, basis.Count > 0 ? basis : null);
            return Results.Ok(new AuthorityTransitionResponse { Stage = input.Stage, PreviousStage = current });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
    }

    private static async Task<IResult> DemoteAsync(
        string id, DemoteAuthorityInput input, IAuthorityStore store, IScenarioStore scenarioStore, CancellationToken ct)
    {
        if (await PhotoSurface.RejectMissingOrMonitoringAsync(scenarioStore, id, "권한 사다리", ct) is { } rejected)
            return rejected;
        try
        {
            var current = (await store.ReadAsync(id, ct))?.Stage ?? AuthorityStage.Advisory;
            if (current == AuthorityStage.Shadow)
                return Results.Problem(statusCode: 409, detail: "이미 최저 단계(shadow)입니다.");
            var target = (AuthorityStage)((int)current - 1);
            await store.SetStageAsync(id, target, input.By ?? "", "demote-manual", input.Reason, ct);
            return Results.Ok(new AuthorityTransitionResponse { Stage = target, PreviousStage = current });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
    }
}
