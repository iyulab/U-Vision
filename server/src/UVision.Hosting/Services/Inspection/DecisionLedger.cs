using System.Collections.Concurrent;
using HoneAI;
using HoneAI.Routing;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Counting;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Ml;
using UVision.Api.Services.Scenarios;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;

namespace UVision.Api.Services.Inspection;

/// <summary>
/// 결정 장부 — 판정마다 HoneAI 계층 라우터 [ML → VLM] 이 같은 답들로 무엇을 정했을지를 provenance 한 줄로
/// 남긴다. <b>관찰 전용</b>이다: 라우터의 결정은 응답·레코드에 쓰이지 않고, 실제로 쓴 답(권한 단계가 정한
/// 출처·자세)은 annotation 으로 나란히 적는다. 정책 스냅숏이 아직 없으므로 모든 조각이 Shadow — 아래 단은
/// 답해도 확정하지 못하고 꼭대기(VLM)가 확정한다. 이 기록이 쌓여야 조각별 신뢰를 재고 관문을 판정할 수 있다.
/// <para>
/// 판정원을 다시 부르지 않는다 — judge 는 파이프라인이 병렬로 이미 받아 둔 답을 돌려준다. 장부 쓰기 실패는
/// 관측 누락일 뿐이라 판정·응답을 막지 않는다(degrade).
/// </para>
/// </summary>
public sealed class DecisionLedger(
    StoragePaths paths,
    IVlmProvider provider,
    IMlClassifier classifier,
    IObjectDetector detector,
    VlmOptions vlmOptions,
    ILogger<DecisionLedger> logger)
{
    /// <summary>
    /// 조각 키 체계 — 유효 시나리오의 <c>trust.slice_key</c>. 지금 지어진 체계는 «시나리오 하나가 조각»뿐이다.
    /// </summary>
    public static ISliceKey<LedgerSubject> SlicesFor(EffectiveScenario scenario) =>
        SliceKey.From<LedgerSubject>(scenario.SliceKey, s => s.Scenario.ScenarioId);

    /// <summary>
    /// 확정 정책 — 유효 시나리오의 클래스에서. 아래 단은 settleable 값만 확정하고, must_catch 값은 언제나 꼭대기로
    /// 간다. 라벨은 대소문자를 가리지 않는다(ML 라벨은 class-agnostic 문자열).
    /// </summary>
    public static ISettlementPolicy<string> PolicyFor(EffectiveScenario scenario) => SettlementPolicy.Categorical(
        settleable: [.. scenario.Classes.Where(c => c.Settleable).Select(c => c.Id)],
        mustCatch: [.. scenario.Classes.Where(c => c.MustCatch).Select(c => c.Id)],
        comparer: StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, JsonlProvenanceSink> _sinks = new(StringComparer.Ordinal);

    /// <summary>
    /// 라우터가 판정원의 답들로 무엇을 정했을지 — 장부 한 줄과 결과 v2 의 <c>path</c> 가 같은 값을 보도록 한 번만
    /// 계산한다. 꼭대기까지 아무도 답하지 않으면 경로만 담은 provenance 를, 라우터 자체가 실패하면 null 을 돌려준다
    /// (관측 누락 — 판정을 막지 않는다).
    /// </summary>
    public async Task<PredictionProvenance?> DecideAsync(LedgerSubject subject, CancellationToken cancellationToken)
    {
        var slices = SlicesFor(subject.Scenario);
        try
        {
            var router = new TieredRouter<LedgerSubject, string>(
                Judges(subject), PolicyFor(subject.Scenario), slices, s => s.DecisionId, snapshot: () => null);
            return (await router.DecideAsync(subject, cancellationToken)).Prediction.Provenance;
        }
        catch (DecisionNotSettledException ex)
        {
            // 꼭대기까지 아무도 답하지 않음(VLM 실패) — 실제 판정도 fail-closed 다. 경로만 남긴다.
            return new PredictionProvenance
            {
                // 계수의 꼭대기는 검출기(학습된 모델)다 — VLM 이 아니다.
                Kind = subject.Scenario.IsCounting ? JudgeKind.Ml : JudgeKind.Vlm,
                SliceKey = slices.KeyOf(subject),
                Path = ex.Path,
                Rationale = "no tier settled",
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "판정 라우터 실패 — 장부 · 경로 누락 (scenario={ScenarioId}, decision={DecisionId})",
                subject.Scenario.ScenarioId, subject.DecisionId);
            return null;
        }
    }

    /// <summary>
    /// 한 판정을 장부에 적는다. <paramref name="operative"/> 는 실제로 응답·레코드에 쓴 결정이다
    /// (fail-closed 면 verdict 없음). <paramref name="provenance"/> 가 null(라우터 실패)이면 적지 않는다.
    /// </summary>
    public async Task RecordAsync(
        LedgerSubject subject, PredictionProvenance? provenance, OperativeDecision operative,
        CancellationToken cancellationToken)
    {
        if (provenance is null)
            return;
        try
        {
            var annotations = new Dictionary<string, string>(operative.ToAnnotations(), StringComparer.Ordinal);
            // 기억 단의 거리 · 이웃 수 · 인코더 판 — 문턱을 재생으로 고르는 원천 자료.
            foreach (var (k, v) in subject.Memory?.ToAnnotations() ?? new Dictionary<string, string>())
                annotations[k] = v;
            // 계수의 근거 — 센 개수 · 기대 · 문턱 · 검출 모델 판(개수 오차를 모델별로 가르는 원천 자료).
            foreach (var (k, v) in subject.Count?.ToAnnotations() ?? new Dictionary<string, string>())
                annotations[k] = v;
            // 시험 데이터셋에서 넣은 판정 — 관문 판정 · 문턱 재생이 현장 데이터와 나눠 볼 수 있게.
            if (subject.Origin is { } origin)
            {
                annotations["origin:kind"] = origin.Kind;
                if (origin.Ref is { } originRef)
                    annotations["origin:ref"] = originRef;
            }
            var record = provenance with { Annotations = annotations };
            var date = StoragePaths.DateBucketOf(subject.Timestamp);
            var sink = _sinks.GetOrAdd(paths.LedgerJsonl(subject.Scenario.ScenarioId, date), p => new JsonlProvenanceSink(p));
            await sink.AppendAsync(subject.DecisionId, record, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "결정 장부 기록 실패 — 관측만 누락 (scenario={ScenarioId}, decision={DecisionId})",
                subject.Scenario.ScenarioId, subject.DecisionId);
        }
    }

    /// <summary>유효 시나리오의 단 순서대로 — 비용 순(아래 → 꼭대기).</summary>
    private IEnumerable<IJudge<LedgerSubject, string>> Judges(LedgerSubject subject)
    {
        // 비용 순위는 단의 종류가 정한다(배열 위치가 아니다) — 같은 단은 시나리오가 달라도 같은 값으로 기록된다.
        foreach (var tier in subject.Scenario.Tiers)
        {
            switch (tier)
            {
                // 결정론적 대조 — 장치가 읽은 코드와 기대 품번이 같으면 일치, 다르면 불일치(놓치면 안 되는 값이라
                // 정책이 위로 보낸다). 코드가 없으면 기권. 기억 · 문턱이 없어 신뢰도는 1.
                case ScenarioVocabulary.MatchTier:
                    yield return new AnsweredJudge(
                        new Tier { Id = tier, Kind = JudgeKind.System1, Version = "exact", CostRank = 0 },
                        MatchJudge.Answer(subject.Context) is { } answer
                            ? JudgeVerdict<string>.Answer(answer, 1.0)
                            : JudgeVerdict<string>.Abstain("no observed code"));
                    break;
                // 확정 사례 기억 — 비활성이면 그 단은 없다(구성에 적혀 있어도). 답은 클래스 id, 기권은 사유와 함께.
                case ScenarioVocabulary.MemoryTier when subject.Memory is { } memory:
                    yield return new AnsweredJudge(
                        new Tier { Id = tier, Kind = JudgeKind.System1, Version = memory.EncoderKey ?? "unknown", CostRank = 0 },
                        memory.Answer is { Class: { } cls } recalled
                            ? JudgeVerdict<string>.Answer(cls, recalled.Confidence)
                            : JudgeVerdict<string>.Abstain(memory.AbstainReason ?? "abstained"));
                    break;
                // ML 비활성이면 그 단은 없다(구성에 적혀 있어도). 활성인데 실패했으면 기권한 단으로 남는다.
                case ScenarioVocabulary.MlTier when subject.Ml is { } ml:
                    yield return new AnsweredJudge(
                        new Tier { Id = tier, Kind = JudgeKind.Ml, Version = ml.Result?.ModelVersion ?? classifier.Name, CostRank = 1 },
                        ml.Result is { } c
                            ? JudgeVerdict<string>.Answer(c.Label, c.Confidence)
                            : JudgeVerdict<string>.Abstain("ml failed: " + ml.FailureReason));
                    break;
                // 계수의 검출기 — 상자를 세어 낸 OK/NG. 검출기가 닿지 않으면(Count 없음) 기권한 단으로 남아 아무도 확정하지 못한다.
                case ScenarioVocabulary.DetectorTier:
                    yield return new AnsweredJudge(
                        new Tier { Id = tier, Kind = JudgeKind.Ml, Version = subject.Count?.ModelSha256 ?? detector.Name, CostRank = 1 },
                        subject.Count is { } count
                            ? JudgeVerdict<string>.Answer(count.Class, count.Confidence, count.Findings)
                            : JudgeVerdict<string>.Abstain("detector unavailable"));
                    break;
                case ScenarioVocabulary.VlmTier:
                    yield return new AnsweredJudge(
                        new Tier { Id = tier, Kind = JudgeKind.Vlm, Version = subject.TeacherEdition ?? $"{provider.Name}:{vlmOptions.Model}", CostRank = 2 },
                        subject.Vlm is { } v
                            ? JudgeVerdict<string>.Answer(subject.Scenario.ClassOf(v) ?? subject.Scenario.ClassOf(v.Verdict), v.Confidence, v.Findings)
                            : JudgeVerdict<string>.Abstain("vlm unavailable"));
                    break;
            }
        }
    }

    /// <summary>파이프라인이 이미 받은 답을 그대로 돌려주는 judge — 판정원을 다시 부르지 않는다.</summary>
    private sealed class AnsweredJudge(Tier tier, JudgeVerdict<string> verdict) : IJudge<LedgerSubject, string>
    {
        public Tier Tier => tier;

        public ValueTask<JudgeVerdict<string>> JudgeAsync(
            LedgerSubject input, JudgeContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(verdict);
    }
}

/// <summary>장부에 적을 판정 한 건 — 판정원이 낸 답들.</summary>
/// <param name="DecisionId">판정 식별자(레코드가 있으면 그 image_id).</param>
/// <param name="Scenario">유효 시나리오 — 정책 · 조각 키 · 단 순서의 출처.</param>
/// <param name="Timestamp">판정 시각(ISO 8601 UTC) — 장부 날짜 버킷.</param>
/// <param name="Vlm">VLM 답(null = 실패).</param>
/// <param name="Ml">ML 결과(null = 비활성).</param>
/// <param name="Context">요청 맥락(기대 품번 · 읽은 코드) — 대조 단의 입력.</param>
/// <param name="Memory">확정 사례 기억의 답 · 기권(null = 비활성).</param>
/// <param name="TeacherEdition">VLM 단의 교사 판(<see cref="TeacherEdition"/>) — null 이면 모델 이름만 적는다.</param>
public sealed record LedgerSubject(
    string DecisionId, EffectiveScenario Scenario, string Timestamp, InspectionResult? Vlm, MlOutcome? Ml,
    InspectionContext? Context = null, MemoryOutcome? Memory = null, CaptureOrigin? Origin = null,
    string? TeacherEdition = null, CountDecision? Count = null);

/// <summary>
/// 계수 시나리오의 판정 — 센 결과와, 그것이 낸 클래스 · 신뢰도 · 소견. 파이프라인이 한 번 정하고 레코드 · 응답 · 장부가 같은 값을 본다.
/// </summary>
/// <param name="Result">개수 세기의 결과(<see cref="CountEvaluator"/>).</param>
/// <param name="Class">판정한 클래스 — 허용 범위 안이고 흔들리지 않으면 <c>OK</c>, 아니면 <c>NG</c>.</param>
/// <param name="Confidence">센 상자 가운데 가장 낮은 신뢰도(가장 약한 근거). 센 상자가 없으면 1 — 흔들림은 <see cref="CountResult.Uncertain"/> 이 말한다.</param>
/// <param name="Findings">사람이 읽는 한 줄 소견.</param>
/// <param name="MinConfidence">센 데 쓴 문턱.</param>
/// <param name="ModelSha256">상자를 낸 모델 파일의 SHA-256(서버가 알려 줄 때만).</param>
public sealed record CountDecision(
    CountResult Result, string Class, double Confidence, string Findings, double MinConfidence, string? ModelSha256)
{
    internal IReadOnlyDictionary<string, string> ToAnnotations()
    {
        var a = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["count:total"] = Result.Total.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["count:min_confidence"] = MinConfidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            ["count:uncertain"] = Result.Uncertain ? "true" : "false",
        };
        if (Result.Expected is { } expected)
            a["count:expected"] = expected.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Result.Tolerance is { } tolerance)
            a["count:tolerance"] = tolerance.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // 구성 확인 — 모자란 개수 · 넘친 개수의 합. 종류 이름을 값에 싣지 않는다(줄 · 구분자 안전, 종류별은 레코드의 count.items).
        if (Result.Items is { } items)
        {
            a["count:missing"] = items.Where(i => i.Status == ItemStatus.Missing).Sum(i => i.Expected - i.Found)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            a["count:extra"] = items.Where(i => i.Status == ItemStatus.Extra).Sum(i => i.Found - i.Expected)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (ModelSha256 is not null)
            a["detector:model_sha256"] = ModelSha256;
        return a;
    }
}

/// <summary>실제로 쓴 결정 — 장부에서 라우터의 결정과 나란히 비교하기 위해 남긴다.</summary>
/// <param name="Verdict">응답한 판정(null = fail-closed).</param>
/// <param name="Source">판정 출처(<c>vlm</c>·<c>ml</c>).</param>
/// <param name="Stage">시나리오 권한 단계.</param>
/// <param name="Posture">자세(<c>review_block</c>·<c>fail_closed</c>, 평상시 null).</param>
/// <param name="Class">응답한 불량 유형 — 시나리오가 NG 를 유형으로 나눌 때만.</param>
public sealed record OperativeDecision(Verdict? Verdict, string Source, string Stage, string? Posture, string? Class = null)
{
    internal IReadOnlyDictionary<string, string> ToAnnotations()
    {
        var a = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operative:verdict"] = Verdict?.ToString() ?? "none",
            ["operative:source"] = Source,
            ["operative:stage"] = Stage,
        };
        if (Posture is not null)
            a["operative:posture"] = Posture;
        if (Class is not null)
            a["operative:class"] = Class;
        return a;
    }
}
