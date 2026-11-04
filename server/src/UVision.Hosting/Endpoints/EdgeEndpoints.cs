using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Edge;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 엣지 노드 신원 엔드포인트(원장 D-102 E-1). 관리자(PIN)는 일회용 등록 토큰을 발급하고 노드를 보고 회수한다. 에이전트는 토큰으로
/// 등록해 노드별 비밀을 받고, 이후 <c>/edge/v1/*</c> 를 <c>Authorization: Bearer</c> 로 부른다. 에이전트는 엔진으로 <b>나가는 방향</b>으로만
/// 연결한다 — 엔진이 노드로 거는 연결은 없다.
/// </summary>
public static class EdgeEndpoints
{
    public static void MapEdgeEndpoints(this RouteGroupBuilder parent)
    {
        parent.MapPost("/edge/tokens", IssueTokenAsync).RequireAdminPin()
            .WithName("IssueEdgeToken")
            .Produces<EdgeTokenResponse>()
            .ProducesProblem(400);
        parent.MapGet("/edge/nodes", ListNodesAsync).RequireAdminPin()
            .WithName("ListEdgeNodes")
            .Produces<IReadOnlyList<EdgeNode>>();
        parent.MapPut("/edge/nodes/{node_id}/scenarios", SetScenariosAsync).RequireAdminPin()
            .WithName("SetEdgeNodeScenarios")
            .Produces<EdgeNode>()
            .ProducesProblem(400)
            .ProducesProblem(404);
        parent.MapDelete("/edge/nodes/{node_id}", RevokeNodeAsync).RequireAdminPin()
            .WithName("RevokeEdgeNode")
            .Produces<EdgeNode>()
            .ProducesProblem(400)
            .ProducesProblem(404);

        // 에이전트 쪽 — 등록은 토큰이 자격이라 인증 필터가 없고, 나머지는 노드 Bearer.
        parent.MapPost("/edge/v1/register", RegisterAsync)
            .WithName("RegisterEdgeNode")
            .Produces<EdgeRegisterResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401);
        parent.MapPost("/edge/v1/events", PostEventsAsync).RequireEdgeNode()
            .WithName("PostEdgeEvents")
            .Produces<EdgeEventsResponse>()
            .ProducesProblem(400);
        parent.MapGet("/edge/v1/whoami", (HttpContext http) => Results.Ok(http.CurrentNode())).RequireEdgeNode()
            .WithName("EdgeWhoAmI")
            .Produces<EdgeNode>();
    }

    private static async Task<IResult> IssueTokenAsync(EdgeTokenRequest? request, EdgeNodeStore nodes, CancellationToken ct)
    {
        try { return Results.Ok(await nodes.IssueTokenAsync(request?.Name, request?.TtlMinutes, ct)); }
        catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
    }

    private static async Task<IResult> ListNodesAsync(EdgeNodeStore nodes, CancellationToken ct) =>
        Results.Ok(await nodes.ListAsync(ct));

    private static async Task<IResult> RevokeNodeAsync(string node_id, EdgeNodeStore nodes, CancellationToken ct)
    {
        try
        {
            return await nodes.RevokeAsync(node_id, ct) is { } node
                ? Results.Ok(node)
                : Results.Problem(statusCode: 404, detail: $"노드 없음: {node_id}");
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, detail: "node_id 형식 위반"); }
    }

    private static async Task<IResult> SetScenariosAsync(
        string node_id, EdgeScenariosInput input, EdgeNodeStore nodes, IScenarioStore scenarios, CancellationToken ct)
    {
        try
        {
            foreach (var id in input.Scenarios.Distinct(StringComparer.Ordinal))
                if (await scenarios.GetAsync(id, ct) is null)
                    return Results.Problem(statusCode: 400, detail: $"시나리오 없음: {id}");
            return await nodes.SetScenariosAsync(node_id, input.Scenarios, ct) is { } node
                ? Results.Ok(node)
                : Results.Problem(statusCode: 404, detail: $"노드 없음: {node_id}");
        }
        catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
    }

    private static async Task<IResult> PostEventsAsync(
        EdgeEventsRequest request, HttpContext http, EdgeEventStore events, CancellationToken ct)
    {
        try { return Results.Ok(await events.IngestAsync(http.CurrentNode(), request, ct)); }
        catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
    }

    private static async Task<IResult> RegisterAsync(EdgeRegisterRequest request, EdgeNodeStore nodes, CancellationToken ct)
    {
        try
        {
            return await nodes.RegisterAsync(request.Token, request.Name, ct) is { } registered
                ? Results.Ok(registered)
                // 틀림 · 이미 씀 · 만료를 가르지 않는다 — 토큰의 존재를 탐색하게 두지 않는다.
                : Results.Problem(statusCode: 401, detail: "등록 토큰이 맞지 않거나 이미 썼거나 만료됨");
        }
        catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
    }
}
