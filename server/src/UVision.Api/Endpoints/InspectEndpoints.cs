using Microsoft.AspNetCore.Mvc;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;

namespace UVision.Api.Endpoints;

/// <summary>
/// 검사 엔드포인트 — 캡처 이미지를 VLM 으로 판정한다.
/// (원본: server/app/api/inspect.py + main.py health)
///
/// 시나리오는 하드코딩 1개("demo"). 저장소 기반 조회는 Phase 2(시나리오 관리)에서 대체한다.
/// 여기서 선제 구현하지 않는다.
/// </summary>
public static class InspectEndpoints
{
    private static readonly HashSet<string> AllowedTypes = new() { "image/jpeg", "image/png" };

    // 임시 시나리오 카탈로그 — Phase 2 에서 저장소로 대체.
    private static readonly Dictionary<string, ScenarioContext> Scenarios = new()
    {
        ["demo"] = new ScenarioContext
        {
            ScenarioId = "demo",
            Criteria =
                "제품 표면에 긁힘, 이물질, 균열, 솔더 브릿지 등 외관 결함이 없어야 한다. " +
                "결함이 보이면 NG, 깨끗하면 OK.",
        },
    };

    public static void MapInspectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", (IVlmProvider provider) =>
            Results.Ok(new { status = "ok", provider = provider.Name }));

        app.MapPost("/api/inspect", InspectAsync)
            .DisableAntiforgery(); // multipart/form-data — API 는 antiforgery 토큰 미사용
    }

    private static async Task<IResult> InspectAsync(
        IFormFile image,
        [FromForm(Name = "scenario_id")] string scenarioId,
        IVlmProvider provider,
        VlmOptions options,
        CancellationToken cancellationToken)
    {
        if (!AllowedTypes.Contains(image.ContentType))
            return Results.Problem(statusCode: 415, detail: $"지원하지 않는 이미지 형식: {image.ContentType}");

        if (!Scenarios.TryGetValue(scenarioId, out var scenario))
            return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}");

        var maxBytes = options.MaxUploadSizeMb * 1024L * 1024L;
        if (image.Length > maxBytes)
            return Results.Problem(statusCode: 413, detail: $"이미지가 너무 큼(>{options.MaxUploadSizeMb}MB)");
        if (image.Length == 0)
            return Results.Problem(statusCode: 400, detail: "빈 이미지");

        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, cancellationToken);
        var data = buffer.ToArray();
        if (data.Length == 0)
            return Results.Problem(statusCode: 400, detail: "빈 이미지");

        var result = await provider.InspectAsync(data, scenario, cancellationToken);

        return Results.Ok(new InspectResponse
        {
            Verdict = result.Verdict,
            Findings = result.Findings,
            Confidence = result.Confidence,
            Timestamp = DateTime.UtcNow.ToString("o"),
            ImageId = $"img_{Guid.NewGuid():N}"[..12], // "img_" + 8 hex (원본 uuid4().hex[:8] 동등)
        });
    }
}
