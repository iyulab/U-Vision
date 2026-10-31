using System.Globalization;
using UVision.Api.Configuration;
using UVision.Api.Imaging;
using UVision.Api.Models;
using UVision.Api.Services.Authority;
using UVision.Api.Services.Confidence;
using UVision.Api.Services.Counting;
using UVision.Api.Services.DualCheck;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Metrics;
using UVision.Api.Services.Ml;
using UVision.Api.Services.Posture;
using UVision.Api.Services.Scenarios;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;

namespace UVision.Api.Services.Inspection;

/// <summary>
/// 판정 파이프라인 — 캡처 한 장을 판정원에 묻고, 판정을 정하고, 기록한다. HTTP 를 모른다:
/// 엔드포인트는 업로드 검증·바이트 읽기와 <see cref="InspectionOutcome"/> → 상태 코드 매핑만 맡는다.
/// <para>
/// 단계는 셋으로 나뉜다. ① <see cref="GatherAsync"/> 판정원 실행 — VLM 과 ML 을 병렬로 부르고 실패를
/// 신호로 흡수한다. ② <see cref="DecideAsync"/> 판정 결정 — 2중체크와 권한 단계로 어느 답을 쓸지 정한다.
/// ③ 기록 — 레코드(must-succeed)·결정 장부·메트릭·자동격하(degrade). 판정원 실행과 결정을 나눠 두는 이유는
/// 결정 규칙을 바꿀 때(예: 계층 라우터) 판정원 호출의 병렬성을 잃지 않기 위해서다.
/// </para>
/// <para>실패 등급: 기준이미지 로드·ML·메트릭·자동격하는 degrade, VLM 은 fail-closed(503), 레코드 저장은
/// must-succeed(500 — 200 위장 금지).</para>
/// </summary>
public sealed class InspectionPipeline(
    IVlmProvider provider,
    IMlClassifier classifier,
    IObjectDetector detector,
    CaseMemory memory,
    IConfidenceCalibrator calibrator,
    IScenarioStore scenarioStore,
    IInspectionStore inspectionStore,
    IAnomalyMapStore anomalyMaps,
    IReferenceStore referenceStore,
    IMetricsStore metricsStore,
    IAuthorityStore authorityStore,
    ILabelStore labelStore,
    DecisionLedger ledger,
    Events.DecisionEventPublisher events,
    VlmOptions vlmOptions,
    MlOptions mlOptions,
    DetectionOptions detectionOptions,
    AuthorityOptions authorityOptions,
    ILogger<InspectionPipeline> logger)
{
    /// <summary>
    /// 시나리오를 찾는다. 형식 위반 id 는 <see cref="ArgumentException"/>(호출부가 400 으로),
    /// 없으면 null(404).
    /// </summary>
    public Task<Scenario?> FindScenarioAsync(string scenarioId, CancellationToken cancellationToken) =>
        scenarioStore.GetAsync(scenarioId, cancellationToken);

    /// <summary>캡처 한 장을 판정하고 기록한다.</summary>
    public async Task<InspectionOutcome> RunAsync(
        Scenario scenario, InspectionCapture capture, CancellationToken cancellationToken)
    {
        var effective = EffectiveScenario.Resolve(scenario, classifier.IsEnabled, memory.IsEnabled);
        if (effective.IsCounting)
            return await RunCountingAsync(scenario, effective, capture, cancellationToken);
        // 전용 ML 은 시나리오의 단에 있을 때만 부른다 — 기본 파생([ml, vlm])이면 지금과 같고, 오투입 확인처럼
        // ML 이 뜻 없는 시나리오는 부르지 않는다.
        var mlActive = classifier.IsEnabled && effective.Tiers.Contains(ScenarioVocabulary.MlTier);
        // 확정 사례 기억도 같다 — 시나리오의 단에 있을 때만(검사에만 있을 수 있다). Shadow 라 판정을 정하지 않고 장부에만 남는다.
        var memoryActive = memory.IsEnabled && effective.Tiers.Contains(ScenarioVocabulary.MemoryTier);
        var evidence = await GatherAsync(scenario, effective, capture, mlActive, memoryActive, cancellationToken);
        var (stage, decision, dual) = await DecideAsync(scenario, effective, evidence, cancellationToken);
        var result = evidence.Vlm;
        var ml = evidence.Ml?.Result;

        // fail-closed: 자동 판정 없음 → StoredResult 미저장, 503(+ML 활성 시 메트릭).
        // posture == FailClosed 로 분기: ml-primary 에서 ML-down 도 포함하는 일반 게이트.
        if (decision.Posture == InspectionPosture.FailClosed)
        {
            logger.LogWarning(
                "판정 불가(fail-closed) (scenario={ScenarioId}, reason={Reason})",
                scenario.ScenarioId, decision.Reason);

            var failedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            await WriteFailClosedMetricSafelyAsync(
                scenario.ScenarioId, ml, failedAt, vlmOnly: !mlActive, MatchOf(effective, capture.Context), capture.Origin?.Kind,
                cancellationToken);
            var failedSubject = new LedgerSubject(
                $"dec_{Guid.NewGuid():N}", effective, failedAt, result, evidence.Ml, capture.Context, evidence.Memory, capture.Origin,
                evidence.TeacherEdition);
            var failedRoute = await ledger.DecideAsync(failedSubject, cancellationToken);
            await ledger.RecordAsync(
                failedSubject, failedRoute,
                new OperativeDecision(null, SourceName(decision), AuthorityStageJsonConverter.ToWire(stage), MetricsRow.PostureFailClosed),
                cancellationToken);

            return new InspectionOutcome.Unavailable(
                new DetectionUnavailableResponse
                {
                    Reason = decision.Reason!,
                    MlHint = decision.VerdictSource == VerdictSource.Ml
                        ? (result is null ? null : new MlResult { Label = result.Verdict.ToString(), Confidence = result.Confidence })
                        : (ml is null ? null : new MlResult { Label = ml.Label, Confidence = ml.Confidence }),
                },
                new InspectResultV2
                {
                    DecisionId = failedSubject.DecisionId,
                    ScenarioId = scenario.ScenarioId,
                    Timestamp = failedAt,
                    Path = PathStepV2.From(failedRoute),
                    DetectionUnavailable = true,
                    Reason = decision.Reason,
                    DeviceId = NullIfEmpty(capture.DeviceId),
                    DeviceLabel = NullIfEmpty(capture.DeviceLabel),
                    CapturedAt = capture.CapturedAt,
                    Origin = capture.Origin?.Kind,
                    OriginRef = capture.Origin?.Ref,
                });
        }

        // verdict 출처 — ml-primary 만 ML, 그 외 VLM. fail-closed 통과 후이므로 출처 측은 non-null.
        var useMl = decision.VerdictSource == VerdictSource.Ml;
        // 클래스가 판정이고 OK/NG 는 그 v1 보기다. 두 판정원의 답은 GatherAsync 가 선언한 클래스로 걸러 두었다.
        var decidedClass = useMl ? effective.ClassFor(ml!.Label)! : effective.ClassOf(result!)!;
        var verdict = effective.VerdictOf(decidedClass);
        var findings = useMl ? "" : result!.Findings;
        var confidence = useMl ? ml!.Confidence : result!.Confidence;

        var imageId = $"img_{Guid.NewGuid():N}";
        var timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var ext = ImageFiles.ExtensionFor(capture.ContentType);

        // SurfaceMl=false(shadow)면 ml/agreement/review 생략 → 응답 byte-identical.
        var mlResult = decision.SurfaceMl && dual is not null
            ? new MlResult { Label = dual.MlLabel, Confidence = dual.MlConfidence, ModelVersion = ml?.ModelVersion }
            : null;
        var agreement = decision.SurfaceMl ? dual?.Agreement : null;
        var requiresReview = decision.SurfaceMl ? dual?.RequiresReview : null;
        var posture = decision.Posture == InspectionPosture.ReviewBlock ? "review_block" : null;
        // must-catch 충돌 — 아래 단이 잡은 값을 위 단이 뒤집었으면 확정하지 않고 사람에게 막아 세운다.
        var contests = MustCatchGuard.Contests(effective, capture.Context, decidedClass);
        if (contests.Count > 0)
        {
            requiresReview = true;
            posture = "review_block";
        }

        var stored = new StoredResult
        {
            ScenarioId = scenario.ScenarioId,
            ImageId = imageId,
            Verdict = verdict,
            Findings = findings,
            Confidence = confidence,
            Timestamp = timestamp,
            ImageFile = imageId + ext,
            DeviceId = capture.DeviceId ?? "",
            DeviceLabel = capture.DeviceLabel ?? "",
            Ml = mlResult,
            Agreement = agreement,
            RequiresReview = requiresReview,
            Posture = posture,
            ExpectedPart = capture.Context?.ExpectedPart,
            ObservedCode = capture.Context?.ObservedCode,
            CapturedAt = capture.CapturedAt,
            Origin = capture.Origin?.Kind,
            OriginRef = capture.Origin?.Ref,
            // 불량 유형을 나누는 시나리오만 — 이분 판정 · 오투입 확인의 레코드는 verdict 가 곧 클래스(byte 보존).
            Class = effective.IsMultiClass ? decidedClass : null,
        };

        // must-succeed: system-of-record 이므로 영속화 실패 시 200 위장 금지(→500).
        try
        {
            await inspectionStore.SaveAsync(capture.Image, ext, stored, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "검사 결과 영속화 실패 (scenario={ScenarioId}, image={ImageId})",
                scenario.ScenarioId, imageId);
            return new InspectionOutcome.RecordFailed();
        }

        // 이상 지도(열지도 근거) — 패치 점수를 쌓는 중일 때만, 관찰 자료라 실패해도 판정을 막지 않는다.
        if (evidence.Memory?.Scores?.Map is { } anomalyMap)
        {
            try
            {
                await anomalyMaps.WriteAsync(
                    scenario.ScenarioId, StoragePaths.DateBucketOf(timestamp), imageId, anomalyMap, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "이상 지도 기록 실패 — 관측만 누락 (scenario={ScenarioId}, image={ImageId})",
                    scenario.ScenarioId, imageId);
            }
        }

        // 결정 장부(관찰 전용) — 레코드가 생긴 뒤에 적어 장부가 없는 레코드를 가리키지 않게 한다.
        var subject = new LedgerSubject(
            imageId, effective, timestamp, result, evidence.Ml, capture.Context, evidence.Memory, capture.Origin,
            evidence.TeacherEdition);
        var route = await ledger.DecideAsync(subject, cancellationToken);
        await ledger.RecordAsync(
            subject, route,
            new OperativeDecision(verdict, SourceName(decision), AuthorityStageJsonConverter.ToWire(stage), posture,
                effective.IsMultiClass ? decidedClass : null),
            cancellationToken);

        // B3 관측성: 판정마다 메트릭 행 하나 — ML 이 없는 ① 단계도 쌓여야 ①→② 전환 근거가 ① 에서 모인다. ML 을 묻지
        // 않은 행은 vlm_only 로 표시해 ML 비율에서 빠진다. 응답 · 레코드는 이 기록과 무관하다(wire 불변).
        // 영속화(must-succeed) 후 기록 — 메트릭은 관측이지 system-of-record 아니므로 실패는 degrade.
        await WriteMetricsSafelyAsync(
            scenario.ScenarioId, imageId, timestamp, verdict, confidence, evidence.Ml, dual,
            posture, vlmOnly: !mlActive, MatchOf(effective, capture.Context), contested: contests.Count > 0,
            capture.Origin?.Kind, cancellationToken);

        if (mlActive
            && (stage == AuthorityStage.CoPrimary || stage == AuthorityStage.MlPrimary))
            await CheckAutoDemoteSafelyAsync(effective, stage, timestamp, cancellationToken);

        // v1 응답은 v2 결과의 OK/NG 보기다 — 같은 판정 · 신뢰도 · 시각 · id 에 v1 에만 있는 2중체크 세부를 더한다.
        var v2 = new InspectResultV2
        {
            DecisionId = imageId,
            ScenarioId = scenario.ScenarioId,
            Timestamp = timestamp,
            Class = decidedClass,
            SettledBy = new SettledBy { Tier = SourceName(decision), Kind = SourceName(decision) },
            Confidence = confidence,
            Findings = useMl ? null : findings,
            RequiresReview = requiresReview,
            Posture = posture,
            ContestedBy = contests.Count > 0 ? contests : null,
            Path = PathStepV2.From(route),
            DeviceId = NullIfEmpty(capture.DeviceId),
            DeviceLabel = NullIfEmpty(capture.DeviceLabel),
            CapturedAt = capture.CapturedAt,
            Origin = capture.Origin?.Kind,
            OriginRef = capture.Origin?.Ref,
        };

        // 판정 사건(P4) — outbox 에 남기기만 한다(보내기는 따로). 관측 · 통합 경로라 실패는 degrade.
        try
        {
            await events.PublishAsync(v2, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "판정 사건 기록 실패 — 판정은 그대로 (scenario={ScenarioId}, image={ImageId})",
                scenario.ScenarioId, imageId);
        }

        return new InspectionOutcome.Completed(
            new InspectResponse
            {
                Verdict = verdict,
                Findings = findings,
                Confidence = confidence,
                Timestamp = v2.Timestamp,
                ImageId = v2.DecisionId,
                Ml = mlResult,
                Agreement = agreement,
                RequiresReview = requiresReview,
                Posture = posture,
            },
            v2);
    }

    /// <summary>
    /// 계수 시나리오의 판정 — VLM 이 아니라 검출기가 상자를 내고, 순수 함수(<see cref="CountEvaluator"/>)가 신뢰도 문턱으로 걸러
    /// 센 수를 기대 개수 ± 허용 오차와 견줘 OK/NG 를 낸다. 단은 검출기 하나라 그것이 확정한다.
    /// <para>
    /// 실패 등급은 검사와 같다: 검출기가 꺼졌거나 답하지 못하면 개수를 모르므로 판정 불가(503, 레코드 없음 — 개수를 모르는 채
    /// OK 를 내지 않는다), 레코드 저장은 must-succeed, 장부 · 사건은 degrade. 문턱 근처 상자가 결론을 바꾸는 사진은 NG 로 두고
    /// 사람 확인(<c>review_block</c>)을 요구한다.
    /// </para>
    /// 메트릭 행은 쓰지 않는다 — 지금의 행은 VLM · ML 의 신뢰도 · 일치를 전제로 한 집계의 입력이라 검출기 점수를 거기 섞으면
    /// 뜻이 흐려진다. 계수의 개수 오차 분포는 장부의 <c>count:*</c> 주석과 레코드의 <c>count</c> 로 읽는다.
    /// </summary>
    private async Task<InspectionOutcome> RunCountingAsync(
        Scenario scenario, EffectiveScenario effective, InspectionCapture capture, CancellationToken cancellationToken)
    {
        string? unavailable = null;
        DetectionSet? detected = null;
        if (effective.ExpectedCount is null && effective.ExpectedByClass is null)
            unavailable = "계수 시나리오에 기대가 없다 — expected_count 또는 expected_by_class(scenario.json 을 고쳐야 한다)";
        else if (!detector.IsEnabled)
            unavailable = "검출기가 꺼져 있다(UVision:Detection:Provider)";
        else
        {
            try
            {
                detected = await detector.DetectAsync(capture.Image, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "검출 실패 — 개수를 알 수 없어 fail-closed(판정 불가) (provider={Provider}, scenario={ScenarioId})",
                    detector.Name, scenario.ScenarioId);
                unavailable = "검출기가 답하지 못했다 — 개수를 알 수 없다";
            }
        }

        var stage = AuthorityStageJsonConverter.ToWire(AuthorityStage.Advisory);
        if (detected is null)
        {
            var failedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var failedSubject = new LedgerSubject(
                $"dec_{Guid.NewGuid():N}", effective, failedAt, null, null, capture.Context, null, capture.Origin, null);
            var failedRoute = await ledger.DecideAsync(failedSubject, cancellationToken);
            await ledger.RecordAsync(
                failedSubject, failedRoute,
                new OperativeDecision(null, ScenarioVocabulary.DetectorTier, stage, MetricsRow.PostureFailClosed),
                cancellationToken);
            return new InspectionOutcome.Unavailable(
                new DetectionUnavailableResponse { Reason = unavailable! },
                new InspectResultV2
                {
                    DecisionId = failedSubject.DecisionId,
                    ScenarioId = scenario.ScenarioId,
                    Timestamp = failedAt,
                    Path = PathStepV2.From(failedRoute),
                    DetectionUnavailable = true,
                    Reason = unavailable,
                    DeviceId = NullIfEmpty(capture.DeviceId),
                    DeviceLabel = NullIfEmpty(capture.DeviceLabel),
                    CapturedAt = capture.CapturedAt,
                    Origin = capture.Origin?.Kind,
                    OriginRef = capture.Origin?.Ref,
                });
        }

        var minConfidence = effective.MinConfidence ?? detectionOptions.MinConfidence;
        // 종류별 기대(구성 확인)와 총 개수 기대는 같은 검출기 단 · 같은 OK/NG 를 쓰고, 기대를 견주는 순수 함수만 다르다.
        var counted = effective.IsComposition
            ? CompositionEvaluator.Evaluate(
                detected.Boxes, minConfidence, effective.CountClasses, effective.ExpectedByClass!, effective.ExtraPolicy!)
            : CountEvaluator.Count(
                detected.Boxes, minConfidence, effective.CountClasses, effective.ExpectedCount, effective.CountTolerance);
        var decidedClass = counted.WithinRange == true && !counted.Uncertain ? ScenarioVocabulary.Ok : ScenarioVocabulary.Ng;
        var verdict = effective.VerdictOf(decidedClass);
        var confidence = counted.Boxes.Count > 0 ? counted.Boxes.Min(b => b.Confidence) : 1.0;
        var findings = FindingsOf(counted, minConfidence, effective.ExtraPolicy);
        var decision = new CountDecision(counted, decidedClass, confidence, findings, minConfidence, detected.ModelSha256);
        var info = new CountInfo
        {
            Total = counted.Total,
            ByClass = counted.ByClass,
            Expected = counted.Expected!.Value,
            Tolerance = counted.Tolerance!.Value,
            MinConfidence = minConfidence,
            Uncertain = counted.Uncertain,
            Boxes = [.. counted.Boxes.Select(BoxOf)],
            NearBoxes = counted.NearBoxes.Count == 0 ? null : [.. counted.NearBoxes.Select(BoxOf)],
            ModelSha256 = detected.ModelSha256,
            Items = counted.Items is null
                ? null
                : [.. counted.Items.Select(i => new CountItemInfo { Class = i.Class, Expected = i.Expected, Found = i.Found, Status = i.Status })],
            ExtraPolicy = counted.Items is null ? null : effective.ExtraPolicy,
        };
        // 흔들리는 사진은 NG 로 두고 확인 전까지 다음 캡처를 막는다 — 개수를 못 믿는 채 흘려보내지 않는다.
        bool? requiresReview = counted.Uncertain ? true : null;
        var posture = counted.Uncertain ? "review_block" : null;

        var imageId = $"img_{Guid.NewGuid():N}";
        var timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var ext = ImageFiles.ExtensionFor(capture.ContentType);
        var stored = new StoredResult
        {
            ScenarioId = scenario.ScenarioId,
            ImageId = imageId,
            Verdict = verdict,
            Findings = findings,
            Confidence = confidence,
            Timestamp = timestamp,
            ImageFile = imageId + ext,
            DeviceId = capture.DeviceId ?? "",
            DeviceLabel = capture.DeviceLabel ?? "",
            RequiresReview = requiresReview,
            Posture = posture,
            CapturedAt = capture.CapturedAt,
            Origin = capture.Origin?.Kind,
            OriginRef = capture.Origin?.Ref,
            Count = info,
        };
        try
        {
            await inspectionStore.SaveAsync(capture.Image, ext, stored, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "검사 결과 영속화 실패 (scenario={ScenarioId}, image={ImageId})", scenario.ScenarioId, imageId);
            return new InspectionOutcome.RecordFailed();
        }

        var subject = new LedgerSubject(
            imageId, effective, timestamp, null, null, capture.Context, null, capture.Origin, null, decision);
        var route = await ledger.DecideAsync(subject, cancellationToken);
        await ledger.RecordAsync(
            subject, route, new OperativeDecision(verdict, ScenarioVocabulary.DetectorTier, stage, posture), cancellationToken);

        var v2 = new InspectResultV2
        {
            DecisionId = imageId,
            ScenarioId = scenario.ScenarioId,
            Timestamp = timestamp,
            Class = decidedClass,
            SettledBy = new SettledBy { Tier = ScenarioVocabulary.DetectorTier, Kind = "ml" },
            Confidence = confidence,
            Findings = findings,
            RequiresReview = requiresReview,
            Posture = posture,
            Count = info,
            Path = PathStepV2.From(route),
            DeviceId = NullIfEmpty(capture.DeviceId),
            DeviceLabel = NullIfEmpty(capture.DeviceLabel),
            CapturedAt = capture.CapturedAt,
            Origin = capture.Origin?.Kind,
            OriginRef = capture.Origin?.Ref,
        };
        try
        {
            await events.PublishAsync(v2, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "판정 사건 기록 실패 — 판정은 그대로 (scenario={ScenarioId}, image={ImageId})",
                scenario.ScenarioId, imageId);
        }

        return new InspectionOutcome.Completed(
            new InspectResponse
            {
                Verdict = verdict,
                Findings = findings,
                Confidence = confidence,
                Timestamp = timestamp,
                ImageId = imageId,
                RequiresReview = requiresReview,
                Posture = posture,
            },
            v2);
    }

    private static CountBox BoxOf(Detection b) =>
        new() { Class = b.Class, X = b.X, Y = b.Y, W = b.W, H = b.H, Confidence = b.Confidence };

    /// <summary>센 결과의 한 줄 소견 — 센 수, 기대와 허용, 문턱, 그리고 왜 NG 인지. 구성 확인이면 모자란 종류와 많은 종류를 짚는다.</summary>
    private static string FindingsOf(CountResult counted, double minConfidence, string? extraPolicy)
    {
        if (counted.Items is { } items)
        {
            var composed = string.Create(CultureInfo.InvariantCulture,
                $"구성 확인 — 센 개수 {counted.Total} (기대 합계 {counted.Expected}, 신뢰도 {minConfidence:0.##} 이상)");
            var missing = items.Where(i => i.Status == ItemStatus.Missing).ToList();
            var extra = items.Where(i => i.Status == ItemStatus.Extra).ToList();
            if (missing.Count > 0)
                composed += " — 누락: " + string.Join(", ", missing.Select(i => $"{i.Class} {i.Expected - i.Found}개 부족"));
            if (extra.Count > 0)
                composed += " — 과잉: " + string.Join(", ", extra.Select(i => $"{i.Class} {i.Found - i.Expected}개 초과"))
                    + (extraPolicy == ExtraPolicies.Ng ? "" : " (판정에는 반영하지 않음)");
            if (counted.Uncertain)
                composed += " — 문턱 근처 상자가 결과를 바꿀 수 있어 사람 확인이 필요하다";
            return composed;
        }

        var text = string.Create(CultureInfo.InvariantCulture,
            $"센 개수 {counted.Total} (기대 {counted.Expected} ± {counted.Tolerance}, 신뢰도 {minConfidence:0.##} 이상)");
        if (counted.WithinRange != true)
            text += " — 허용 범위 밖";
        if (counted.Uncertain)
            text += " — 문턱 근처 상자가 결과를 바꿀 수 있어 사람 확인이 필요하다";
        return text;
    }

    /// <summary>
    /// ① 판정원 실행 — 기준이미지를 싣고 VLM(주 판정) · 전용 ML(교차검증) · 확정 사례 기억(Shadow)을 병렬로 부른다.
    /// VLM 실패는 null(fail-closed 신호), ML 비활성은 null, ML 실패는 <see cref="MlOutcome.Failed"/>, 기억 비활성은 null
    /// (기억의 실패는 기권으로 담긴다).
    /// </summary>
    private async Task<JudgeEvidence> GatherAsync(
        Scenario scenario, EffectiveScenario effective, InspectionCapture capture, bool mlActive, bool memoryActive,
        CancellationToken cancellationToken)
    {
        var image = capture.Image;
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
            logger.LogWarning(ex, "기준이미지 로드 실패 — few-shot 없이 진행 (scenario={ScenarioId})",
                scenario.ScenarioId);
        }

        // 교사 판은 디스크의 기준 이미지 내용으로 센다 — 다운스케일 사본이 아니라(그 레버는 판 문자열의 설정 쪽에 든다).
        var loadedReferences = references;

        // 다운스케일 레버(scenario.MaxImageDimension) — VLM 전송 사본에만 적용한다.
        // 원본은 그대로 영속화(audit 충실도·재평가 여지). query+refs 대칭(refs 는 JPEG 로 정규화).
        // maxDim=0(기본)이면 원본을 그대로 보내 불필요한 복사·재인코딩을 피한다(핫패스).
        var vlmImage = image;
        if (scenario.MaxImageDimension > 0)
        {
            vlmImage = ImageDownscaler.Downscale(image, scenario.MaxImageDimension);
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
            ExpectedPart = capture.Context?.ExpectedPart,
            DefectClasses = effective.IsMultiClass ? effective.DefectTypes : null,
        };

        // ML 은 원본으로 분류한다(MLoop 자체 전처리; VLM 다운스케일 사본 아님).
        var vlmTask = InspectSafelyAsync(vlmImage, context, cancellationToken);
        Task<MlOutcome>? classifyTask = mlActive
            ? ClassifySafelyAsync(image, scenario.ScenarioId, cancellationToken)
            : null;
        // 기억도 원본으로 — 기억에 든 확정 사례도 원본에서 임베딩했다.
        Task<MemoryOutcome>? recallTask = memoryActive
            ? memory.RecallAsync(effective, image, cancellationToken)
            : null;
        await Task.WhenAll(
            [vlmTask, .. (classifyTask is not null ? [classifyTask] : Array.Empty<Task>()),
                .. (recallTask is not null ? [recallTask] : Array.Empty<Task>())]);

        return new JudgeEvidence(
            AcceptVlm(await vlmTask, effective),
            classifyTask is not null ? AcceptMl(await classifyTask, effective) : null,
            recallTask is not null ? await recallTask : null,
            TeacherEdition.Of(
                $"{provider.Name}:{vlmOptions.Model}", vlmOptions, context, scenario.MaxImageDimension, loadedReferences));
    }

    /// <summary>
    /// VLM 답을 시나리오의 클래스로 거른다 — 유형을 나누는 시나리오에서 NG 에 선언한 유형을 대지 못한 답은 답이 아니다
    /// (꼭대기의 무응답 = fail-closed, 사람 확인). 받은 답은 유형 id 를 선언한 철자로 맞춘다.
    /// </summary>
    private InspectionResult? AcceptVlm(InspectionResult? result, EffectiveScenario effective)
    {
        if (result is null || !effective.IsMultiClass) return result;
        if (effective.ClassOf(result) is not { } cls)
        {
            logger.LogWarning(
                "VLM 이 선언하지 않은 불량 유형으로 답함 — fail-closed (scenario={ScenarioId}, defect_class={DefectClass})",
                effective.ScenarioId, result.DefectClass);
            return null;
        }
        return result with { DefectClass = result.Verdict == Verdict.NG ? cls : null };
    }

    /// <summary>
    /// ML 라벨을 시나리오의 클래스로 거른다 — 선언에 없는 라벨(모델과 시나리오가 다른 클래스를 배움)은 판정에 쓰지 않고
    /// 분류 실패와 같이 다룬다(degrade — VLM 단독).
    /// </summary>
    private MlOutcome AcceptMl(MlOutcome outcome, EffectiveScenario effective)
    {
        if (outcome.Result is not { } c || effective.ClassFor(c.Label) is not null) return outcome;
        logger.LogWarning(
            "ML 이 시나리오에 없는 클래스로 답함 — VLM 단독 진행(degrade) (scenario={ScenarioId}, label={Label})",
            effective.ScenarioId, c.Label);
        return MlOutcome.Failure($"시나리오에 없는 클래스: {c.Label}");
    }

    /// <summary>
    /// ② 판정 결정 — 2중체크(둘 다 답했을 때)와 시나리오의 권한 단계로 판정 자세·출처를 정한다.
    /// 권한 단계 읽기 실패는 Advisory(현재 동작)로 폴백한다.
    /// </summary>
    private async Task<(AuthorityStage Stage, AuthorityDecision Decision, DualCheckResult? Dual)> DecideAsync(
        Scenario scenario, EffectiveScenario effective, JudgeEvidence evidence, CancellationToken cancellationToken)
    {
        var result = evidence.Vlm;
        var ml = evidence.Ml?.Result;
        var dual = (result is not null && ml is not null)
            ? DualCheckEvaluator.Evaluate(
                result, effective.ClassOf(result)!, ml, mlOptions.ReviewConfidenceThreshold, calibrator)
            : null;

        AuthorityState? authorityState = null;
        try { authorityState = await authorityStore.ReadAsync(scenario.ScenarioId, cancellationToken); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "권한 단계 읽기 실패 — Advisory 폴백 (scenario={ScenarioId})", scenario.ScenarioId);
        }
        var stage = AuthorityResolver.Resolve(authorityState);

        var decision = AuthorityLadder.Evaluate(
            stage, vlmSucceeded: result is not null, mlSucceeded: ml is not null, dual);
        return (stage, decision, dual);
    }

    /// <summary>
    /// VLM 판정을 호출하되 실패를 삼킨다 — ML 과 대칭. 실패 시 경고 로그 + null 반환(fail-closed 신호).
    /// VLM 은 must-succeed 검출원이나, 예외가 unhandled 500 로 새지 않도록 여기서 잡아 자세 사다리가
    /// 정의된 503(판정 불가)으로 매핑하게 한다(③.5 E2). 시간 한도(<see cref="VlmOptions.TimeoutSeconds"/>)는
    /// provider 설정이 아니라 여기서 건다 — provider 와 무관한 판정 한 건의 총 예산이고, SDK 재시도도 그 안에 든다.
    /// </summary>
    private async Task<InspectionResult?> InspectSafelyAsync(
        ReadOnlyMemory<byte> image, ScenarioContext context, CancellationToken cancellationToken)
    {
        using var limit = vlmOptions.TimeoutSeconds > 0 ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        limit?.CancelAfter(TimeSpan.FromSeconds(vlmOptions.TimeoutSeconds));
        try
        {
            return await provider.InspectAsync(image, context, limit?.Token ?? cancellationToken);
        }
        catch (OperationCanceledException) when (limit is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "VLM 판정 시간 한도 {TimeoutSeconds}초 초과 — fail-closed(판정 불가) (provider={Provider}, scenario={ScenarioId})",
                vlmOptions.TimeoutSeconds, provider.Name, context.ScenarioId);
            return null;
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
    /// ML 분류를 호출하되 실패를 삼킨다(degrade) — references 로드와 동일 규율: ML 오류가 판정을
    /// 막지 않는다. 실패 시 경고 로그 + <see cref="MlOutcome.Failed"/> 반환(비활성과 구분).
    /// </summary>
    private async Task<MlOutcome> ClassifySafelyAsync(
        ReadOnlyMemory<byte> image, string scenarioId, CancellationToken cancellationToken)
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
    /// fail-closed 메트릭 행을 기록하되 실패를 삼킨다(관측 — degrade-safe). verdict 없는 행이라
    /// <c>posture="fail_closed"</c>·verdict/vlm_confidence=null. ML 을 묻지 않았으면 vlm_only.
    /// </summary>
    private async Task WriteFailClosedMetricSafelyAsync(
        string scenarioId, MlClassification? ml, string timestamp, bool vlmOnly, string? match, string? origin,
        CancellationToken cancellationToken)
    {
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
            VlmOnly = vlmOnly,
            Match = match,
            Origin = origin,
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
    /// B3 메트릭 row 를 기록하되 실패를 삼킨다(degrade) — 영속화(must-succeed)와 달리 메트릭은 관측이라
    /// 쓰기 실패가 판정·응답(이미 확정·영속됨)을 막지 않는다. 실패 시 경고 로그만 남긴다.
    /// <para>
    /// row 는 <paramref name="outcome"/> 에서 구성한다: 분류 성공(<c>outcome.Result</c>)이면 dual 의
    /// ml/agreement/review 를 채우고, degrade(<c>outcome.Failed</c>)면 ml_* 를 null·<c>ml_degraded=true</c>.
    /// ML 을 묻지 않은 판정(<paramref name="vlmOnly"/>)은 outcome · dual 이 null 이라 ml_* 가 비고 vlm_only 로 남는다.
    /// </para>
    /// <para>
    /// Shadow 단계(<c>SurfaceMl=false</c>)에서는 응답에서 <c>dual?.MlLabel</c>·<c>Agreement</c>·
    /// <c>RequiresReview</c> 가 생략되지만, 메트릭에는 그대로 기록된다 — 침묵 플라이휠 데이터 수집.
    /// </para>
    /// </summary>
    private async Task WriteMetricsSafelyAsync(
        string scenarioId, string imageId, string timestamp,
        Verdict verdict, double confidence, MlOutcome? outcome, DualCheckResult? dual,
        string? posture, bool vlmOnly, string? match, bool contested, string? origin, CancellationToken cancellationToken)
    {
        var degraded = outcome?.Failed ?? false;
        var row = new MetricsRow
        {
            ImageId = imageId,
            Timestamp = timestamp,
            Verdict = verdict,
            VlmConfidence = confidence,
            MlLabel = dual?.MlLabel,
            MlConfidence = dual?.MlConfidence,
            Agreement = dual?.Agreement,
            RequiresReview = dual?.RequiresReview,
            MlDegraded = degraded,
            Posture = posture, // review_block 관측(B3) — null 이면 생략, 집계는 비-fail_closed 로 카운트
            VlmOnly = vlmOnly,
            Match = match,
            Contested = contested,
            Origin = origin,
        };
        try { await metricsStore.AppendAsync(scenarioId, row, cancellationToken); }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "메트릭 기록 실패 — 관측만 누락 (scenario={ScenarioId}, image={ImageId})", scenarioId, imageId);
        }
    }

    /// <summary>대조 단의 답(메트릭용) — 시나리오에 대조 단이 없으면 null, 읽은 코드가 없으면 기권.</summary>
    private static string? MatchOf(EffectiveScenario effective, InspectionContext? context) =>
        effective.Tiers.Contains(ScenarioVocabulary.MatchTier)
            ? MatchJudge.Answer(context) ?? Services.Metrics.MetricsAggregator.MatchAbstained
            : null;

    /// <summary>
    /// A1 자동격하: ML 영향 단계(co-primary/ml-primary)에서 N건마다 재집계 → 열화 시 안전쪽 1단계 하향.
    /// degrade-safe — 실패는 관측만 누락, 판정·응답 무차단. 격상은 사람(PIN)이라 여기서 안 함.
    /// </summary>
    private async Task CheckAutoDemoteSafelyAsync(
        EffectiveScenario effective, AuthorityStage stage, string timestamp, CancellationToken cancellationToken)
    {
        var scenarioId = effective.ScenarioId;
        try
        {
            var dateBucket = StoragePaths.DateBucketOf(timestamp);
            var rows = await metricsStore.ReadAsync(scenarioId, dateBucket, cancellationToken);
            if (!ShouldCheckAutoDemote(rows.Count, authorityOptions.AutoDemoteCheckEvery))
                return;

            var labels = await labelStore.ListAsync(scenarioId, dateBucket, cancellationToken);
            var summary = MetricsAggregator.Summarize(
                scenarioId, dateBucket, rows, labels, authorityOptions, effective.NgLabelRule);
            if (AutoDemoteEvaluator.Evaluate(stage, summary, authorityOptions) is { } target)
            {
                await authorityStore.SetStageAsync(
                    scenarioId, target, "system", "demote-auto",
                    $"ml_ng_recall={summary.MlNgRecall} below floor/vlm", cancellationToken);
                logger.LogWarning(
                    "자동격하 — ML 열화 감지 (scenario={ScenarioId}, {From}→{To})", scenarioId, stage, target);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "자동격하 체크 실패 — 관측만 누락 (scenario={ScenarioId})", scenarioId);
        }
    }

    /// <summary>자동격하 안전체크를 이번 inspect 에서 수행할지(암종화 — N건마다 1회). 0건/비배수는 skip.</summary>
    public static bool ShouldCheckAutoDemote(int todayRowCount, int checkEvery) =>
        todayRowCount > 0 && checkEvery > 0 && todayRowCount % checkEvery == 0;

    private static string SourceName(AuthorityDecision decision) =>
        decision.VerdictSource == VerdictSource.Ml ? "ml" : "vlm";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>판정원 실행 결과 — VLM(null=실패) · ML(null=비활성) · 기억(null=비활성) · 이 판정을 낸 교사 판.</summary>
    private sealed record JudgeEvidence(InspectionResult? Vlm, MlOutcome? Ml, MemoryOutcome? Memory, string TeacherEdition);
}

/// <summary>판정할 캡처 한 장 — 업로드 검증을 통과한 바이트와 그 출처.</summary>
/// <param name="Image">원본 바이트(영속화·ML 은 이것을, VLM 은 레버에 따라 다운스케일 사본을 쓴다).</param>
/// <param name="ContentType">검증된 형식(<c>image/jpeg</c>·<c>image/png</c>) — 저장 확장자를 정한다.</param>
/// <param name="DeviceId">촬영 장치 식별자(선택).</param>
/// <param name="DeviceLabel">촬영 장치 표시 이름(선택).</param>
/// <param name="Context">요청 맥락 — 오투입 확인의 기대 품번 · 읽은 코드(선택).</param>
/// <param name="CapturedAt">장치가 캡처한 시각, 정규화된 UTC ISO 8601(선택 — <see cref="CaptureTime"/>).</param>
/// <param name="Origin">캡처 출처 — 시험 데이터셋 주입이면(선택, 없으면 현장 촬영 — <see cref="CaptureOrigin"/>).</param>
public sealed record InspectionCapture(
    ReadOnlyMemory<byte> Image, string ContentType, string? DeviceId, string? DeviceLabel,
    InspectionContext? Context = null, string? CapturedAt = null, CaptureOrigin? Origin = null);

/// <summary>판정 파이프라인의 결과 — 호출부(HTTP·다른 진입점)가 자기 표현으로 옮긴다.</summary>
public abstract record InspectionOutcome
{
    private InspectionOutcome() { }

    /// <summary>판정·기록 완료.</summary>
    /// <param name="Response">v1 응답(OK/NG 보기).</param>
    /// <param name="V2">결과 v2.</param>
    public sealed record Completed(InspectResponse Response, InspectResultV2 V2) : InspectionOutcome;

    /// <summary>자동 판정 불가(fail-closed) — 레코드를 남기지 않았다.</summary>
    public sealed record Unavailable(DetectionUnavailableResponse Response, InspectResultV2 V2) : InspectionOutcome;

    /// <summary>판정은 났으나 레코드 저장 실패 — 판정을 돌려주지 않는다(must-succeed).</summary>
    public sealed record RecordFailed : InspectionOutcome;
}
