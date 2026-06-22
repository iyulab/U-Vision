using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UVision.Api.Configuration;
using UVision.Api.Imaging;
using UVision.Api.Models;
using UVision.Api.Services.Confidence;
using UVision.Api.Services.DualCheck;
using UVision.Api.Services.Ml;
using UVision.Api.Services.Posture;
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
        IMetricsStore metricsStore,
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
        // ML 비활성(기본 none)이면 mlTask=null(MlOutcome 아님) → VLM 단독 경로(현재 동작 무변경).
        // ③.5 E2: VLM 은 InspectSafelyAsync 로 래핑 — 예외가 unhandled 500 으로 새지 않고
        // null(fail-closed 신호)로 흡수된다. `result is null` ↔ FailClosed 동치(컴파일러 흐름분석).
        var logger = loggerFactory.CreateLogger(typeof(InspectEndpoints));
        var vlmTask = InspectSafelyAsync(provider, vlmImage, context, logger, cancellationToken);
        // null = 비활성(none). MlOutcome 은 활성 경로에서만 생성 — 비활성은 outcome 자체가 null.
        Task<MlOutcome>? classifyTask = classifier.IsEnabled
            ? ClassifySafelyAsync(classifier, data, scenario.ScenarioId, logger, cancellationToken)
            : null;
        await Task.WhenAll(
            [vlmTask, .. (classifyTask is not null ? [classifyTask] : Array.Empty<Task>())]);
        var result = await vlmTask;            // null = VLM 실패(fail-closed)
        var outcome = classifyTask is not null ? await classifyTask : null;

        var ml = outcome?.Result;
        var dual = (result is not null && ml is not null)
            ? DualCheckEvaluator.Evaluate(result, ml, mlOptions.ReviewConfidenceThreshold, calibrator)
            : null;

        var decision = DegradeLadder.Evaluate(vlmSucceeded: result is not null, dual);

        // fail-closed: 자동 판정 없음 → StoredResult 미저장, 503 반환(+ML 활성 시 메트릭).
        // `result is null` 로 분기 — FailClosed ⟺ result null 동치이며, 이렇게 해야 컴파일러
        // 흐름분석이 이후 코드에서 result 를 non-null 로 좁혀 기존 `result.Verdict` 접근에 경고가 없다.
        if (result is null)
        {
            logger.LogWarning(
                "판정 불가(fail-closed) — VLM 검출원 사용 불가, 사람 확인 필요 (scenario={ScenarioId}, reason={Reason})",
                scenario.ScenarioId, decision.Reason);

            if (classifier.IsEnabled)
                await WriteFailClosedMetricSafelyAsync(
                    metricsStore, scenario.ScenarioId, ml, logger, cancellationToken);

            return Results.Json(new DetectionUnavailableResponse
            {
                Reason = decision.Reason!,
                MlHint = ml is null ? null : new MlResult { Label = ml.Label, Confidence = ml.Confidence },
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

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

        // B3 관측성: ML 활성(②~③ 결선)일 때만 예측 신호를 메트릭 시계열로 흘린다.
        // 비활성(none=①단계)이면 메트릭도 없음 → inspect 부수효과가 ② 이전과 동일.
        // 영속화(must-succeed) 후 기록 — 메트릭은 관측이지 system-of-record 아니므로 실패는 degrade.
        if (classifier.IsEnabled)
            await WriteMetricsSafelyAsync(
                metricsStore, scenario.ScenarioId, imageId, timestamp, result, outcome, dual,
                logger, cancellationToken);

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
    /// VLM 판정을 호출하되 실패를 삼킨다 — ML 과 대칭. 실패 시 경고 로그 + null 반환(fail-closed 신호).
    /// VLM 은 must-succeed 검출원이나, 예외가 unhandled 500 로 새지 않도록 여기서 잡아 자세 사다리가
    /// 정의된 503(판정 불가)으로 매핑하게 한다(③.5 E2).
    /// </summary>
    private static async Task<InspectionResult?> InspectSafelyAsync(
        IVlmProvider provider, ReadOnlyMemory<byte> image, ScenarioContext context,
        ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.InspectAsync(image, context, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "VLM 판정 실패 — fail-closed(판정 불가) (provider={Provider}, scenario={ScenarioId})",
                provider.Name, context.ScenarioId);
            return null;
        }
    }

    /// <summary>
    /// fail-closed 메트릭 행을 기록하되 실패를 삼킨다(관측 — degrade-safe). verdict 없는 행이라
    /// <c>posture="fail_closed"</c>·verdict/vlm_confidence=null. ML 활성 경로에서만 호출.
    /// </summary>
    private static async Task WriteFailClosedMetricSafelyAsync(
        IMetricsStore metricsStore, string scenarioId, MlClassification? ml,
        ILogger logger, CancellationToken cancellationToken)
    {
        var timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var row = new MetricsRow
        {
            ImageId = $"img_{Guid.NewGuid():N}",
            Timestamp = timestamp,
            Verdict = null,
            VlmConfidence = null,
            MlLabel = ml?.Label,
            MlConfidence = ml?.Confidence,
            Posture = MetricsRow.PostureFailClosed,
            MlDegraded = false,
        };
        try
        {
            await metricsStore.AppendAsync(scenarioId, row, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "fail-closed 메트릭 기록 실패 — 관측만 누락 (scenario={ScenarioId})", scenarioId);
        }
    }

    /// <summary>
    /// ML 분류를 호출하되 실패를 삼킨다(degrade) — references 로드와 동일 규율: ML 오류가 판정을
    /// 막지 않는다. 실패 시 경고 로그 + <see cref="MlOutcome.Failed"/> 반환(비활성과 구분).
    /// </summary>
    private static async Task<MlOutcome> ClassifySafelyAsync(
        IMlClassifier classifier, ReadOnlyMemory<byte> image, string scenarioId,
        ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var c = await classifier.ClassifyAsync(image, scenarioId, cancellationToken);
            return MlOutcome.Success(c);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ML 분류 실패 — 2중체크 없이 VLM 단독 진행(degrade) (provider={Provider}, scenario={ScenarioId})",
                classifier.Name, scenarioId);
            return MlOutcome.Failure(ex.Message);
        }
    }

    /// <summary>
    /// B3 메트릭 row 를 기록하되 실패를 삼킨다(degrade) — 영속화(must-succeed)와 달리 메트릭은 관측이라
    /// 쓰기 실패가 판정·응답(이미 확정·영속됨)을 막지 않는다. 실패 시 경고 로그만 남긴다.
    /// <para>
    /// row 는 <paramref name="outcome"/> 에서 구성한다: 분류 성공(<c>outcome.Result</c>)이면 dual 의
    /// ml/agreement/review 를 채우고, degrade(<c>outcome.Failed</c>)면 ml_* 를 null·<c>ml_degraded=true</c>.
    /// 이 메서드는 ML enabled 경로에서만 호출된다(비활성은 outcome 이 null → 호출 안 함).
    /// </para>
    /// </summary>
    private static async Task WriteMetricsSafelyAsync(
        IMetricsStore metricsStore, string scenarioId, string imageId, string timestamp,
        InspectionResult result, MlOutcome? outcome, DualCheckResult? dual,
        ILogger logger, CancellationToken cancellationToken)
    {
        var degraded = outcome?.Failed ?? false;
        var row = new MetricsRow
        {
            ImageId = imageId,
            Timestamp = timestamp,
            Verdict = result.Verdict,
            VlmConfidence = result.Confidence,
            MlLabel = dual?.MlLabel,
            MlConfidence = dual?.MlConfidence,
            Agreement = dual?.Agreement,
            RequiresReview = dual?.RequiresReview,
            MlDegraded = degraded,
        };

        try
        {
            await metricsStore.AppendAsync(scenarioId, row, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "메트릭 기록 실패 — 관측만 누락(판정·영속은 정상) (scenario={ScenarioId}, image={ImageId})",
                scenarioId, imageId);
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
