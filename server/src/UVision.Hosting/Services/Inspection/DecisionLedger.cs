using System.Collections.Concurrent;
using HoneAI;
using HoneAI.Routing;
using UVision.Api.Configuration;
using UVision.Api.Models;
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
                Kind = JudgeKind.Vlm,
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
    string? TeacherEdition = null);

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
