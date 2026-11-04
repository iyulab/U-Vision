using UVision.Api.Models;
using UVision.Api.Services.Edge;

namespace UVision.Api.Auth;

/// <summary>
/// 엣지 노드 인증 필터 — <c>Authorization: Bearer {node_id}.{secret}</c> 를 <see cref="EdgeNodeStore"/> 로 확인한다(원장 D-102 E-1).
/// 틀린 · 모르는 · <b>회수된</b> 노드는 모두 같은 401 이다(노드 존재를 알려 주지 않는다). 노드 자격은 관리자 PIN 이 아니라서
/// <c>/edge/v1/*</c> 밖의 어떤 경로도 열지 못한다. 통과한 노드는 <see cref="EdgeNodeAuthExtensions.CurrentNode"/> 로 읽는다.
/// </summary>
public sealed class EdgeNodeFilter(EdgeNodeStore nodes) : IEndpointFilter
{
    internal const string ItemKey = "uvision.edge.node";
    private const string Scheme = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            || await nodes.AuthenticateAsync(header[Scheme.Length..].Trim(), context.HttpContext.RequestAborted) is not { } node)
            return Results.Problem(statusCode: 401, detail: "노드 자격이 맞지 않거나 회수됨");

        context.HttpContext.Items[ItemKey] = node;
        return await next(context);
    }
}

public static class EdgeNodeAuthExtensions
{
    /// <summary>노드 Bearer 인증을 엔드포인트에 붙인다.</summary>
    public static RouteHandlerBuilder RequireEdgeNode(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<RouteHandlerBuilder, EdgeNodeFilter>().ProducesProblem(StatusCodes.Status401Unauthorized);

    /// <summary>필터를 통과한 노드 — 필터 없이 부르면 예외(배선 실수를 조용히 넘기지 않는다).</summary>
    public static EdgeNode CurrentNode(this HttpContext context) =>
        context.Items[EdgeNodeFilter.ItemKey] as EdgeNode
        ?? throw new InvalidOperationException("RequireEdgeNode 없이 노드를 읽었다");
}
