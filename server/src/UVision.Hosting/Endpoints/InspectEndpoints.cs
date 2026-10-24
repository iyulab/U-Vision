using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Inspection;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 검사 엔드포인트 — 업로드를 검증해 <see cref="InspectionPipeline"/> 에 넘기고 결과를 HTTP 로 옮긴다.
///
/// 시나리오는 <see cref="IScenarioStore"/>(파일시스템)에서 조회한다 — S-A 에서 하드코딩 <c>demo</c>
/// 카탈로그를 대체. <c>/api/inspect</c> 의 wire 계약(요청/응답 스키마)은 불변 — 영속화 부수효과를 더할 뿐.
/// </summary>
public static class InspectEndpoints
{
    public static void MapInspectEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/health", (IVlmProvider provider) =>
            Results.Ok(new HealthResponse { Status = "ok", Provider = provider.Name }))
            .WithName("GetHealth")
            .Produces<HealthResponse>();

        group.MapPost("/inspect", InspectAsync)
            .DisableAntiforgery() // multipart/form-data — API 는 antiforgery 토큰 미사용
            .WithName("Inspect")
            .Produces<InspectResponse>()
            .Produces<DetectionUnavailableResponse>(503)
            .ProducesUploadProblems()
            .ProducesProblem(404)
            .ProducesProblem(422) // Idempotency-Key 가 이미 다른 요청에 쓰였다
            .ProducesProblem(500);

        // 결과 v2 — 같은 요청, 시나리오 클래스 · 확정자 · 라우터 경로를 담은 응답. v1 은 그대로 둔다.
        group.MapPost("/v2/inspect", InspectV2Async)
            .DisableAntiforgery()
            .WithName("InspectV2")
            .Produces<InspectResultV2>()
            .Produces<InspectResultV2>(503)
            .ProducesUploadProblems()
            .ProducesProblem(404)
            .ProducesProblem(422) // Idempotency-Key 가 이미 다른 요청에 쓰였다
            .ProducesProblem(500);

        group.MapGet("/results", ListResultsAsync)
            .WithName("ListResults")
            .Produces<IReadOnlyList<StoredResult>>()
            .ProducesProblem(400);
        group.MapGet("/results/dates", ListResultDatesAsync)
            .WithName("ListResultDates")
            .Produces<IReadOnlyList<string>>()
            .ProducesProblem(400);
        group.MapGet("/results/image", ServeResultImageAsync)
            .WithName("GetResultImage")
            .Produces(200, typeof(Stream), "image/jpeg", "image/png")
            .ProducesProblem(400)
            .ProducesProblem(404);
    }

    private static Task<IResult> InspectAsync(
        IFormFile image,
        [FromForm(Name = "scenario_id")] string scenarioId,
        [FromForm(Name = "device_id")] string? deviceId,
        [FromForm(Name = "device_label")] string? deviceLabel,
        [FromForm(Name = "captured_at")] string? capturedAt,
        [FromHeader(Name = InspectionReplays.KeyHeader)] string? idempotencyKey,
        InspectionPipeline pipeline,
        InspectionReplays replays,
        VlmOptions options,
        CancellationToken cancellationToken) =>
        RunAsync(
            "inspect", image, scenarioId, deviceId, deviceLabel, capturedAt, context: null, origin: null, idempotencyKey,
            outcome => outcome switch
            {
                InspectionOutcome.Completed c => (Results.Ok(c.Response), c.Response),
                InspectionOutcome.Unavailable u => (Results.Json(u.Response, statusCode: StatusCodes.Status503ServiceUnavailable), null),
                InspectionOutcome.RecordFailed => (Results.Problem(statusCode: 500, detail: "검사 결과 저장 실패"), null),
                _ => throw new InvalidOperationException($"처리하지 않은 판정 결과: {outcome.GetType().Name}"),
            },
            pipeline, replays, options, cancellationToken);

    private static async Task<IResult> InspectV2Async(
        IFormFile image,
        [FromForm(Name = "scenario_id")] string scenarioId,
        [FromForm(Name = "device_id")] string? deviceId,
        [FromForm(Name = "device_label")] string? deviceLabel,
        [FromForm(Name = "expected_part")] string? expectedPart,
        [FromForm(Name = "observed_code")] string? observedCode,
        [FromForm(Name = "captured_at")] string? capturedAt,
        [FromForm(Name = "origin")] string? origin,
        [FromForm(Name = "origin_ref")] string? originRef,
        [FromHeader(Name = InspectionReplays.KeyHeader)] string? idempotencyKey,
        InspectionPipeline pipeline,
        InspectionReplays replays,
        VlmOptions options,
        CancellationToken cancellationToken)
    {
        var invalidContext = InspectionContext.Validate(expectedPart, "expected_part")
            ?? InspectionContext.Validate(observedCode, "observed_code")
            ?? CaptureOrigin.Validate(origin, originRef);
        if (invalidContext is not null)
            return Results.Problem(statusCode: 400, detail: invalidContext);
        var context = expectedPart is null && observedCode is null
            ? null
            : new InspectionContext { ExpectedPart = expectedPart, ObservedCode = observedCode };

        var captureOrigin = origin is null ? null : new CaptureOrigin { Kind = origin, Ref = originRef };

        return await RunAsync(
            "v2/inspect", image, scenarioId, deviceId, deviceLabel, capturedAt, context, captureOrigin, idempotencyKey,
            outcome => outcome switch
            {
                InspectionOutcome.Completed c => (Results.Ok(c.V2), c.V2),
                InspectionOutcome.Unavailable u => (Results.Json(u.V2, statusCode: StatusCodes.Status503ServiceUnavailable), null),
                InspectionOutcome.RecordFailed => (Results.Problem(statusCode: 500, detail: "검사 결과 저장 실패"), null),
                _ => throw new InvalidOperationException($"처리하지 않은 판정 결과: {outcome.GetType().Name}"),
            },
            pipeline, replays, options, cancellationToken);
    }

    /// <summary>
    /// 두 판(v1 · v2)이 같은 요청 검증 · 시나리오 조회 · 판정을 지난다 — 다른 것은 판정 결과를 응답으로 옮기는
    /// <paramref name="respond"/> 뿐이다(그 둘째 값이 재전송 때 돌려줄 성공 본문). 멱등 키가 있으면 판정을
    /// <see cref="InspectionReplays"/> 로 감싼다 — 키가 없으면 지금과 같은 경로다.
    /// </summary>
    private static async Task<IResult> RunAsync(
        string operation, IFormFile image, string scenarioId, string? deviceId, string? deviceLabel, string? capturedAt,
        InspectionContext? context, CaptureOrigin? origin, string? idempotencyKey,
        Func<InspectionOutcome, (IResult Result, object? Replayable)> respond,
        InspectionPipeline pipeline, InspectionReplays replays, VlmOptions options, CancellationToken cancellationToken)
    {
        if (idempotencyKey is not null && !InspectionReplays.IsValidKey(idempotencyKey))
            return Results.Problem(statusCode: 400,
                detail: $"{InspectionReplays.KeyHeader} 는 영문 · 숫자 · '.' '_' '-' 로 된 128자 이하여야 한다");
        var invalid = ImageUpload.Validate(image, options.MaxUploadSizeMb);
        if (invalid is not null)
            return invalid;
        if (!CaptureTime.TryNormalize(capturedAt, out var capturedAtUtc))
            return Results.Problem(statusCode: 400, detail: "captured_at 은 시간대가 있는 ISO 8601 시각이어야 한다");

        Scenario? scenario;
        try
        {
            scenario = await pipeline.FindScenarioAsync(scenarioId, cancellationToken);
        }
        catch (ArgumentException) // 형식 위반 id → 400(존재하지 않음 404 와 구분)
        {
            return Results.Problem(statusCode: 400, detail: $"유효하지 않은 scenario_id: {scenarioId}");
        }
        if (scenario is null)
            return Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}");

        // 오투입 확인은 기대 품번 없이는 판정할 것이 없다 — v1 은 이 필드가 없으므로 v2 로만 부를 수 있다.
        if (scenario.Type == ScenarioVocabulary.Verification && string.IsNullOrWhiteSpace(context?.ExpectedPart))
            return Results.Problem(statusCode: 400,
                detail: "verification 시나리오는 expected_part 가 필요하다(POST /v2/inspect)");

        var data = await ImageUpload.ReadBytesAsync(image, cancellationToken);
        if (data is null)
            return Results.Problem(statusCode: 400, detail: "빈 이미지");

        var capture = new InspectionCapture(data, image.ContentType, deviceId, deviceLabel, context, capturedAtUtc, origin);
        if (idempotencyKey is null)
            return respond(await pipeline.RunAsync(scenario, capture, cancellationToken)).Result;

        var fingerprint = InspectionReplays.Fingerprint(
            operation, data, scenario.ScenarioId, image.ContentType, deviceId, deviceLabel,
            context?.ExpectedPart, context?.ObservedCode, capturedAtUtc);
        return await replays.RunAsync(
            scenario.ScenarioId, idempotencyKey, fingerprint,
            async () => respond(await pipeline.RunAsync(scenario, capture, cancellationToken)),
            cancellationToken);
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

    // 결과 조회 UI 지원(무인증 읽기 — 기존 /api/results 정책과 일치).

    private static async Task<IResult> ListResultDatesAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        IInspectionStore inspectionStore,
        CancellationToken cancellationToken)
    {
        try
        {
            var dates = await inspectionStore.ListDatesAsync(scenarioId, cancellationToken);
            return Results.Ok(dates);
        }
        catch (ArgumentException) // 형식 위반 scenario_id → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id");
        }
    }

    private static async Task<IResult> ServeResultImageAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        [FromQuery(Name = "date")] string date,
        [FromQuery(Name = "image_id")] string imageId,
        IInspectionStore inspectionStore,
        CancellationToken cancellationToken)
    {
        try
        {
            var image = await inspectionStore.ReadImageAsync(scenarioId, date, imageId, cancellationToken);
            return image is null
                ? Results.Problem(statusCode: 404, detail: "검사 이미지 없음")
                : Results.File(image.Data, image.ContentType); // inline — <img src> 직접 사용
        }
        catch (ArgumentException) // 형식 위반 scenario_id/date/image_id → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id/date/image_id");
        }
    }
}
