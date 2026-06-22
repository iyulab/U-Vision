using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Configuration;
using UVision.Api.Imaging;
using UVision.Api.Models;
using UVision.Api.Services.Confidence;
using UVision.Api.Services.DualCheck;
using UVision.Api.Services.Ml;
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
    public static void MapInspectEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/health", (IVlmProvider provider) =>
            Results.Ok(new { status = "ok", provider = provider.Name }));

        group.MapPost("/inspect", InspectAsync)
            .DisableAntiforgery(); // multipart/form-data — API 는 antiforgery 토큰 미사용

        group.MapGet("/results", ListResultsAsync);
        group.MapGet("/results/dates", ListResultDatesAsync);
        group.MapGet("/results/image", ServeResultImageAsync);
    }

    private static async Task<IResult> InspectAsync(
        IFormFile image,
        [FromForm(Name = "scenario_id")] string scenarioId,
        [FromForm(Name = "device_id")] string? deviceId,
        [FromForm(Name = "device_label")] string? deviceLabel,
        IVlmProvider provider,
        IMlClassifier classifier,
        IConfidenceCalibrator calibrator,
        IScenarioStore scenarioStore,
        IInspectionStore inspectionStore,
        IReferenceStore referenceStore,
        VlmOptions options,
        MlOptions mlOptions,
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
        // 라벨당 장수는 시나리오 ReferenceCap(레버, 0=zero-shot). 음수 방어 clamp(잘못된 scenario.json).
        IReadOnlyList<ReferenceImage> references = [];
        try
        {
            references = await referenceStore.LoadImagesAsync(
                scenario.ScenarioId, scenario.NgLabels, Math.Max(0, scenario.ReferenceCap), cancellationToken);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(InspectEndpoints))
                .LogWarning(ex, "기준이미지 로드 실패 — few-shot 없이 진행 (scenario={ScenarioId})",
                    scenario.ScenarioId);
        }

        // 다운스케일 레버(scenario.MaxImageDimension) — VLM 전송 사본에만 적용한다.
        // 원본 `data` 는 그대로 영속화(audit 충실도·재평가 여지). query+refs 대칭(refs 는 JPEG 로 정규화).
        // maxDim=0(기본)이면 원본을 그대로 보내 불필요한 복사·재인코딩을 피한다(핫패스).
        ReadOnlyMemory<byte> vlmImage = data;
        if (scenario.MaxImageDimension > 0)
        {
            vlmImage = ImageDownscaler.Downscale(data, scenario.MaxImageDimension);
            references = references
                .Select(r => r with
                {
                    Data = ImageDownscaler.Downscale(r.Data, scenario.MaxImageDimension),
                    IsPng = false,
                })
                .ToList();
        }

        var context = new ScenarioContext
        {
            ScenarioId = scenario.ScenarioId,
            Name = scenario.Name,
            Criteria = scenario.Criteria,
            References = references,
        };

        // ③ 2중체크: VLM(주 판정) 과 전용 ML(교차검증) 을 병렬 호출한다(Task.WhenAll).
        // ML 은 원본 `data` 로 분류한다(MLoop 자체 전처리; VLM 다운스케일 사본 아님).
        // ML 비활성(기본 none)이면 mlTask=null → VLM 단독 경로(현재 동작 무변경).
        var logger = loggerFactory.CreateLogger(typeof(InspectEndpoints));
        var vlmTask = provider.InspectAsync(vlmImage, context, cancellationToken);
        var mlTask = classifier.IsEnabled
            ? ClassifySafelyAsync(classifier, data, scenario.ScenarioId, logger, cancellationToken)
            : Task.FromResult<MlClassification?>(null);
        await Task.WhenAll(vlmTask, mlTask);
        var result = await vlmTask;
        var ml = await mlTask;

        // ML 결과가 있으면 교차검증 산출(없으면 — 비활성/실패 — additive 필드 생략 = VLM 단독).
        var dual = ml is null
            ? null
            : DualCheckEvaluator.Evaluate(result, ml, mlOptions.ReviewConfidenceThreshold, calibrator);

        // image_id/timestamp 는 한 번 생성하여 영속화 stem 과 응답이 동일하도록 보장한다.
        var imageId = $"img_{Guid.NewGuid():N}"; // "img_" + 32 hex — 멀티태블릿 동일 dir 충돌 방지(절단 제거)
        var timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var ext = ImageUpload.ExtensionFor(image.ContentType);

        var mlResult = dual is null
            ? null
            : new MlResult { Label = dual.MlLabel, Confidence = dual.MlConfidence };

        var stored = new StoredResult
        {
            ScenarioId = scenario.ScenarioId,
            ImageId = imageId,
            Verdict = result.Verdict,
            Findings = result.Findings,
            Confidence = result.Confidence,
            Timestamp = timestamp,
            ImageFile = imageId + ext,
            DeviceId = deviceId ?? "",
            DeviceLabel = deviceLabel ?? "",
            Ml = mlResult,
            Agreement = dual?.Agreement,
            RequiresReview = dual?.RequiresReview,
        };

        // must-succeed: system-of-record 이므로 영속화 실패 시 200 위장 금지(→500).
        try
        {
            await inspectionStore.SaveAsync(data, ext, stored, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "검사 결과 영속화 실패 (scenario={ScenarioId}, image={ImageId})",
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
            Ml = mlResult,
            Agreement = dual?.Agreement,
            RequiresReview = dual?.RequiresReview,
        });
    }

    /// <summary>
    /// ML 분류를 호출하되 실패를 삼킨다(degrade) — references 로드와 동일 규율: ML 오류가 판정을
    /// 막지 않는다. 실패 시 경고 로그 + null 반환 → 호출 측은 VLM 단독으로 진행한다.
    /// </summary>
    private static async Task<MlClassification?> ClassifySafelyAsync(
        IMlClassifier classifier, ReadOnlyMemory<byte> image, string scenarioId,
        ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await classifier.ClassifyAsync(image, scenarioId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ML 분류 실패 — 2중체크 없이 VLM 단독 진행 (provider={Provider}, scenario={ScenarioId})",
                classifier.Name, scenarioId);
            return null;
        }
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
