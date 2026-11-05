using Microsoft.AspNetCore.Mvc;
using UVision.Api.Auth;
using UVision.Api.Configuration;
using UVision.Api.Imaging;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 기준 이미지 갤러리(ROADMAP Phase 2 S-D) — OK/NG 기준 이미지 업로드·서빙·삭제 + NG 레이블.
/// 읽기(목록·이미지)는 무인증(갤러리 미리보기), 변경(업로드·삭제)은 관리자 PIN.
/// few-shot 결합은 <c>/api/inspect</c> 가 소비한다(빌드까지 — 판정효과 M0.1).
/// </summary>
public static class ReferenceEndpoints
{
    public static void MapReferenceEndpoints(this RouteGroupBuilder parent)
    {
        var group = parent.MapGroup("/scenarios/{id}/references");

        group.MapGet("", ListAsync)
            .WithName("ListReferences")
            .Produces<IEnumerable<ReferenceInfo>>()
            .ProducesProblem(400)
            .ProducesProblem(404);
        group.MapGet("/{label}/{refId}", ServeAsync)
            .WithName("GetReferenceImage")
            .Produces(200, typeof(Stream), "image/jpeg", "image/png")
            .ProducesProblem(400)
            .ProducesProblem(404);
        group.MapPost("", UploadAsync).DisableAntiforgery().RequireAdminPin()
            .WithName("UploadReference")
            .Produces<ReferenceInfo>(201)
            .ProducesUploadProblems()
            .ProducesProblem(404)
            .ProducesProblem(409);
        group.MapDelete("/{label}/{refId}", DeleteAsync).RequireAdminPin()
            .WithName("DeleteReference")
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(404);
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

            // 선별은 클래스 폴더를 훑는다 — 응답의 label 은 클래스의 OK/NG 보기, class 가 본체(검사의 응답 모양은 그대로).
            if (scenario.Type == ScenarioVocabulary.Classification)
            {
                var classes = EffectiveScenario.Resolve(scenario, mlEnabled: false).Classes;
                var rows = await referenceStore.ListClassesAsync(id, ct);
                return Results.Ok(rows.Select(r =>
                {
                    var declared = classes.FirstOrDefault(c => string.Equals(c.Id, r.ClassDir, StringComparison.OrdinalIgnoreCase))?.Id ?? r.ClassDir;
                    return new ReferenceInfo
                    {
                        RefId = r.RefId,
                        Label = ScenarioClass.ViewOf(classes, declared) == Verdict.NG ? ReferenceLabel.Ng : ReferenceLabel.Ok,
                        Class = declared,
                    };
                }));
            }

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
        string id, string label, string refId, IScenarioStore scenarioStore, IReferenceStore referenceStore, CancellationToken ct)
    {
        // 선별 시나리오는 label 이 선언한 클래스 id 다(ok|ng 도 그 시나리오가 선언한 클래스일 때만). 그 밖은 지금 그대로다.
        var (isClassification, classId) = await ClassOfAsync(scenarioStore, id, label, ct);
        if (isClassification)
        {
            if (classId is null)
                return Results.Problem(statusCode: 400, detail: $"label 은 시나리오가 선언한 클래스 id: {label}");
            try
            {
                var classBytes = await referenceStore.ReadClassAsync(id, classId, refId, ct);
                return classBytes is null
                    ? Results.Problem(statusCode: 404, detail: "기준 이미지 없음")
                    : Results.File(classBytes.Data, classBytes.ContentType);
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: 400, detail: "유효하지 않은 id");
            }
        }
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
        HttpRequest request,
        UVisionOptions uvision,
        CancellationToken ct,
        [FromForm(Name = "ng_label")] string? ngLabel = null)
    {
        // 선별 시나리오는 label 에 선언한 클래스 id 를 받는다. ng_label(불량 유형 이름)은 검사의 개념이라 받지 않는다.
        var (isClassification, classId) = await ClassOfAsync(scenarioStore, id, label, ct);
        if (isClassification)
            return classId is null
                ? Results.Problem(statusCode: 400, detail: $"label 은 시나리오가 선언한 클래스 id: {label}")
                : await UploadClassAsync(id, classId, image, ngLabel, scenarioStore, referenceStore, options, request, uvision, ct);
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
            if (PhotoSurface.RejectMonitoring(scenario, "기준 이미지") is { } monitoring)
                return monitoring;

            var data = await ImageUpload.ReadBytesAsync(image, ct);
            if (data is null)
                return Results.Problem(statusCode: 400, detail: "빈 이미지");

            var ext = ImageFiles.ExtensionFor(image.ContentType);
            var refId = await referenceStore.SaveAsync(id, parsed, data, ext, ct);

            // NG 기준 이미지면 레이블을 scenario.json 에 기록.
            if (parsed == ReferenceLabel.Ng && !string.IsNullOrWhiteSpace(ngLabel))
                await scenarioStore.SetNgLabelAsync(id, refId, ngLabel.Trim(), ct);

            return Results.Created(ApiLocation.For(request, uvision, $"scenarios/{id}/references/{label}/{refId}"),
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
        var (isClassification, classId) = await ClassOfAsync(scenarioStore, id, label, ct);
        if (isClassification)
        {
            if (classId is null)
                return Results.Problem(statusCode: 400, detail: $"label 은 시나리오가 선언한 클래스 id: {label}");
            try
            {
                return await referenceStore.DeleteClassAsync(id, classId, refId, ct)
                    ? Results.NoContent()
                    : Results.Problem(statusCode: 404, detail: "기준 이미지 없음");
            }
            catch (ArgumentException)
            {
                return Results.Problem(statusCode: 400, detail: "유효하지 않은 id");
            }
        }
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

    /// <summary>
    /// 선별 시나리오인가, 그렇다면 label 이 선언한 클래스 id 인가(대소문자 무시 — 선언한 철자로 돌려준다, 선언 밖이면 null).
    /// 선별이 아니거나 시나리오가 없거나 형식 위반이면 (false, null) — 호출부가 지금의 ok|ng 경로(와 그 오류)로 간다.
    /// </summary>
    private static async Task<(bool IsClassification, string? ClassId)> ClassOfAsync(
        IScenarioStore scenarioStore, string id, string label, CancellationToken ct)
    {
        try
        {
            var scenario = await scenarioStore.GetAsync(id, ct);
            return scenario?.Type == ScenarioVocabulary.Classification
                ? (true, EffectiveScenario.Resolve(scenario, mlEnabled: false).ClassFor(label))
                : (false, null);
        }
        catch (ArgumentException)
        {
            return (false, null);
        }
    }

    private static async Task<IResult> UploadClassAsync(
        string id, string classId, IFormFile image, string? ngLabel, IScenarioStore scenarioStore, IReferenceStore referenceStore,
        VlmOptions options, HttpRequest request, UVisionOptions uvision, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(ngLabel))
            return Results.Problem(statusCode: 400, detail: "ng_label 은 검사 시나리오의 불량 유형 이름이다 — 선별의 기준 이미지는 label 에 클래스 id 를 준다");
        var invalid = ImageUpload.Validate(image, options.MaxUploadSizeMb);
        if (invalid is not null)
            return invalid;
        var scenario = await scenarioStore.GetAsync(id, ct);
        if (scenario is null)
            return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {id}");
        var data = await ImageUpload.ReadBytesAsync(image, ct);
        if (data is null)
            return Results.Problem(statusCode: 400, detail: "빈 이미지");
        var refId = await referenceStore.SaveClassAsync(id, classId, data, ImageFiles.ExtensionFor(image.ContentType), ct);
        var classes = EffectiveScenario.Resolve(scenario, mlEnabled: false).Classes;
        return Results.Created(ApiLocation.For(request, uvision, $"scenarios/{id}/references/{classId}/{refId}"),
            new ReferenceInfo
            {
                RefId = refId,
                Label = ScenarioClass.ViewOf(classes, classId) == Verdict.NG ? ReferenceLabel.Ng : ReferenceLabel.Ok,
                Class = classId,
            });
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
