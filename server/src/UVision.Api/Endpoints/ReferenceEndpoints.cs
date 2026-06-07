using Microsoft.AspNetCore.Mvc;
using UVision.Api.Auth;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 기준 이미지 갤러리(ROADMAP Phase 2 S-D) — OK/NG 기준 이미지 업로드·서빙·삭제 + NG 레이블.
/// 읽기(목록·이미지)는 무인증(갤러리 미리보기), 변경(업로드·삭제)은 관리자 PIN.
/// few-shot 결합은 <c>/api/inspect</c> 가 소비한다(빌드까지 — 판정효과 M0.1).
/// </summary>
public static class ReferenceEndpoints
{
    public static void MapReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scenarios/{id}/references");

        group.MapGet("", ListAsync);
        group.MapGet("/{label}/{refId}", ServeAsync);
        group.MapPost("", UploadAsync).DisableAntiforgery().RequireAdminPin();
        group.MapDelete("/{label}/{refId}", DeleteAsync).RequireAdminPin();
    }

    private static async Task<IResult> ListAsync(
        string id, IScenarioStore scenarioStore, IReferenceStore referenceStore,
        CancellationToken ct)
    {
        try
        {
            var scenario = await scenarioStore.GetAsync(id, ct);
            if (scenario is null)
                return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}");

            var refs = await referenceStore.ListAsync(id, ct);
            // NG 레이블을 scenario.json 에서 결합.
            var withLabels = refs.Select(r => r with
            {
                NgLabel = r.Label == ReferenceLabel.Ng && scenario.NgLabels.TryGetValue(r.RefId, out var l)
                    ? l
                    : null,
            });
            return Results.Ok(withLabels);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
    }

    private static async Task<IResult> ServeAsync(
        string id, string label, string refId, IReferenceStore referenceStore, CancellationToken ct)
    {
        if (!TryParseLabel(label, out var parsed))
            return Results.Problem(statusCode: 400, detail: $"label 은 ok|ng: {label}");
        try
        {
            var bytes = await referenceStore.ReadAsync(id, parsed, refId, ct);
            return bytes is null
                ? Results.Problem(statusCode: 404, detail: "기준 이미지 없음")
                : Results.File(bytes.Data, bytes.ContentType);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 id");
        }
    }

    private static async Task<IResult> UploadAsync(
        string id,
        IFormFile image,
        [FromForm] string label,
        IScenarioStore scenarioStore,
        IReferenceStore referenceStore,
        VlmOptions options,
        CancellationToken ct,
        [FromForm(Name = "ng_label")] string? ngLabel = null)
    {
        if (!TryParseLabel(label, out var parsed))
            return Results.Problem(statusCode: 400, detail: $"label 은 ok|ng: {label}");

        var invalid = ImageUpload.Validate(image, options.MaxUploadSizeMb);
        if (invalid is not null)
            return invalid;

        try
        {
            var scenario = await scenarioStore.GetAsync(id, ct);
            if (scenario is null)
                return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}");

            var data = await ImageUpload.ReadBytesAsync(image, ct);
            if (data is null)
                return Results.Problem(statusCode: 400, detail: "빈 이미지");

            var ext = ImageUpload.ExtensionFor(image.ContentType);
            var refId = await referenceStore.SaveAsync(id, parsed, data, ext, ct);

            // NG 기준 이미지면 레이블을 scenario.json 에 기록.
            if (parsed == ReferenceLabel.Ng && !string.IsNullOrWhiteSpace(ngLabel))
                await scenarioStore.SetNgLabelAsync(id, refId, ngLabel.Trim(), ct);

            return Results.Created($"/api/scenarios/{id}/references/{label}/{refId}",
                new ReferenceInfo { RefId = refId, Label = parsed, NgLabel = ngLabel?.Trim() });
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 시나리오 id: {id}");
        }
    }

    private static async Task<IResult> DeleteAsync(
        string id, string label, string refId,
        IScenarioStore scenarioStore, IReferenceStore referenceStore, CancellationToken ct)
    {
        if (!TryParseLabel(label, out var parsed))
            return Results.Problem(statusCode: 400, detail: $"label 은 ok|ng: {label}");
        try
        {
            var deleted = await referenceStore.DeleteAsync(id, parsed, refId, ct);
            if (!deleted)
                return Results.Problem(statusCode: 404, detail: "기준 이미지 없음");

            // NG 레이블 orphan 제거.
            if (parsed == ReferenceLabel.Ng)
                await scenarioStore.SetNgLabelAsync(id, refId, null, ct);

            return Results.NoContent();
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 id");
        }
    }

    private static bool TryParseLabel(string label, out ReferenceLabel parsed)
    {
        switch (label)
        {
            case "ok":
                parsed = ReferenceLabel.Ok;
                return true;
            case "ng":
                parsed = ReferenceLabel.Ng;
                return true;
            default:
                parsed = default;
                return false;
        }
    }
}
