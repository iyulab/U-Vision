using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 검사 엔드포인트 — 캡처 이미지를 VLM 으로 판정하고 파일시스템에 영속화한다.
///
/// 시나리오는 <see cref="IScenarioStore"/>(파일시스템)에서 조회한다 — S-A 에서 하드코딩 <c>demo</c>
/// 카탈로그를 대체. <c>/api/inspect</c> 의 wire 계약(요청/응답 스키마)은 불변 — 영속화 부수효과를 더할 뿐.
/// </summary>
public static class InspectEndpoints
{
    /// <summary>
    /// few-shot 기준 이미지를 label 당 최대 이 개수만 결합한다. 매 inspect 가 base64 로 기준 이미지를
    /// 전송하므로 이 값이 건별 추론 비용·latency 를 좌우한다(M0.1 비용 측정 대상). 늘리기 전 실측 필요.
    /// </summary>
    private const int MaxReferencesPerLabel = 4;

    public static void MapInspectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", (IVlmProvider provider) =>
            Results.Ok(new { status = "ok", provider = provider.Name }));

        app.MapPost("/api/inspect", InspectAsync)
            .DisableAntiforgery(); // multipart/form-data — API 는 antiforgery 토큰 미사용

        app.MapGet("/api/results", ListResultsAsync);
    }

    private static async Task<IResult> InspectAsync(
        IFormFile image,
        [FromForm(Name = "scenario_id")] string scenarioId,
        IVlmProvider provider,
        IScenarioStore scenarioStore,
        IInspectionStore inspectionStore,
        IReferenceStore referenceStore,
        VlmOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var invalid = ImageUpload.Validate(image, options.MaxUploadSizeMb);
        if (invalid is not null)
            return invalid;

        Scenario? scenario;
        try
        {
            scenario = await scenarioStore.GetAsync(scenarioId, cancellationToken);
        }
        catch (ArgumentException) // 형식 위반 id → 400(존재하지 않음 404 와 구분)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 scenario_id: {scenarioId}");
        }
        if (scenario is null)
            return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}");

        var data = await ImageUpload.ReadBytesAsync(image, cancellationToken);
        if (data is null)
            return Results.Problem(statusCode: 400, detail: "빈 이미지");

        // few-shot 기준 이미지 로드 — 실패는 판정을 막지 않는다(degrade: zero-shot 으로 진행).
        IReadOnlyList<ReferenceImage> references = [];
        try
        {
            references = await referenceStore.LoadImagesAsync(
                scenario.ScenarioId, scenario.NgLabels, MaxReferencesPerLabel, cancellationToken);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(InspectEndpoints))
                .LogWarning(ex, "기준이미지 로드 실패 — few-shot 없이 진행 (scenario={ScenarioId})",
                    scenario.ScenarioId);
        }

        var context = new ScenarioContext
        {
            ScenarioId = scenario.ScenarioId,
            Name = scenario.Name,
            Criteria = scenario.Criteria,
            References = references,
        };
        var result = await provider.InspectAsync(data, context, cancellationToken);

        // image_id/timestamp 는 한 번 생성하여 영속화 stem 과 응답이 동일하도록 보장한다.
        var imageId = $"img_{Guid.NewGuid():N}"[..12]; // "img_" + 8 hex
        var timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var ext = ImageUpload.ExtensionFor(image.ContentType);

        var stored = new StoredResult
        {
            ScenarioId = scenario.ScenarioId,
            ImageId = imageId,
            Verdict = result.Verdict,
            Findings = result.Findings,
            Confidence = result.Confidence,
            Timestamp = timestamp,
            ImageFile = imageId + ext,
        };

        // must-succeed: system-of-record 이므로 영속화 실패 시 200 위장 금지(→500).
        try
        {
            await inspectionStore.SaveAsync(data, ext, stored, cancellationToken);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(InspectEndpoints))
                .LogError(ex, "검사 결과 영속화 실패 (scenario={ScenarioId}, image={ImageId})",
                    scenario.ScenarioId, imageId);
            return Results.Problem(statusCode: 500, detail: "검사 결과 저장 실패");
        }

        return Results.Ok(new InspectResponse
        {
            Verdict = result.Verdict,
            Findings = result.Findings,
            Confidence = result.Confidence,
            Timestamp = timestamp,
            ImageId = imageId,
        });
    }

    private static async Task<IResult> ListResultsAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IInspectionStore inspectionStore,
        CancellationToken cancellationToken,
        [FromQuery(Name = "date")] string? date = null)
    {
        // date 생략 시 오늘(UTC) — 운영의 가장 흔한 질의("오늘 검사 이력").
        date ??= DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        try
        {
            var results = await inspectionStore.ListAsync(scenarioId, date, cancellationToken);
            return Results.Ok(results);
        }
        catch (ArgumentException) // 형식 위반 scenario_id/date → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 date");
        }
    }
}
