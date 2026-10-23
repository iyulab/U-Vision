using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;

namespace UVision.Api.Services.Metrics;

/// <summary>
/// 메트릭 row + 사람 라벨을 시나리오·날짜 집계로 환원하는 순수 함수(B3 read).
/// 병렬 호출·저장소 I/O 는 엔드포인트가 소유하고, 여기서는 카운팅/비율 산출만 한다
/// (<see cref="Services.DualCheck.DualCheckEvaluator"/> 와 동일한 순수-로직 분리 규율 — 결정론·테스트 용이).
/// </summary>
public static class MetricsAggregator
{
    private const string NgLabel = "NG";

    /// <summary>메트릭 행 <c>match</c> 의 기권 값.</summary>
    public const string MatchAbstained = "abstained";

    /// <param name="rows">해당 시나리오·날짜의 메트릭 row(예측 신호).</param>
    /// <param name="labels">같은 날짜 버킷의 사람 라벨(정답) — image_id 로 조인.</param>
    /// <param name="options">권한 이양 레버(A1) — 격상 자격 임계.</param>
    /// <param name="isNg">
    /// 라벨(사람 · ML)이 NG 쪽인가 — 불량 유형을 나누는 시나리오는 모든 유형이 NG 다(<see cref="Scenarios.EffectiveScenario.VerdictOf"/>).
    /// 생략하면 이분 판정(<c>NG</c>, 대소문자 무시). recall 은 «불량을 불량으로 잡았나»라 유형이 달라도 맞힌 것으로 센다.
    /// </param>
    public static MetricsSummary Summarize(
        string scenarioId, string date,
        IReadOnlyList<MetricsRow> rows, IReadOnlyList<StoredLabel> labels,
        AuthorityOptions options, Func<string, bool>? isNg = null)
    {
        isNg ??= IsBinaryNg;
        // 라벨 일관성 집계(C1) — audit 상태별 카운팅. NG recall 루프와 독립.
        // 오라클↔사람 비교(④-B) — operative·oracle 둘 다 있는 건만(순수 파생, 항상 최신). 같은 패스에서 집계.
        int audited = 0, labelConsistent = 0, labelConflictsOpen = 0;
        int oracleEvaluated = 0, oracleConflicts = 0;
        foreach (var l in labels)
        {
            switch (l.Audit?.Status ?? LabelAuditStatus.Unaudited)
            {
                case LabelAuditStatus.Consistent: audited++; labelConsistent++; break;
                case LabelAuditStatus.Conflicted: audited++; labelConflictsOpen++; break;
                case LabelAuditStatus.Resolved: audited++; break;
                // unaudited → 집계 제외
            }

            if (l.OperativeLabel is { } op && l.LatestOracleOpinion is { } oracle)
            {
                oracleEvaluated++;
                if (!string.Equals(op, oracle, StringComparison.OrdinalIgnoreCase))
                    oracleConflicts++;
            }
        }

        // image_id → 사람 라벨(정답). operative 라벨만(oracle-only 제외 — GT 없음).
        // 마지막 값 우선(라벨은 last-write-wins 사이드카).
        var labelOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var oracledImages = new HashSet<string>(StringComparer.Ordinal); // 오라클 소견이 기록된 image_id(커버리지 조인).
        foreach (var l in labels)
        {
            if (l.OperativeLabel is { } op)
                labelOf[l.ImageId] = op;
            if (l.LatestOracleOpinion is not null)
                oracledImages.Add(l.ImageId);
        }

        int failClosed = 0;
        int inspections = 0;  // 비-fail-closed (rows.Count 대신)
        int mlChecked = 0;    // 그 가운데 ML 을 물은 건 — ML 비율의 분모
        int matchMismatches = 0, matchAbstained = 0, contested = 0;
        int degraded = 0, agreements = 0, reviews = 0, oraclePending = 0;
        int labeled = 0, labeledNg = 0, vlmNgHits = 0, mlNgScored = 0, mlNgHits = 0;

        foreach (var r in rows)
        {
            if (r.Posture == MetricsRow.PostureFailClosed)
            {
                failClosed++;
                continue;  // verdict 없는 행 — 기존 집계 제외
            }
            inspections++;
            if (r.Match == ScenarioVocabulary.Mismatch) matchMismatches++;
            else if (r.Match == MatchAbstained) matchAbstained++;
            if (r.Contested) contested++;

            if (r.VlmOnly)
            {
                // ML 을 묻지 않은 판정 — ML 비율에는 들지 않고, VLM recall 만 센다.
            }
            else if (r.MlDegraded)
            {
                mlChecked++;
                degraded++;
            }
            else
            {
                mlChecked++;
                if (r.Agreement == true) agreements++;
                if (r.RequiresReview == true)
                {
                    reviews++;
                    if (!oracledImages.Contains(r.ImageId)) oraclePending++; // 검토 대상인데 오라클 미처리 = 백로그.
                }
            }

            if (!labelOf.TryGetValue(r.ImageId, out var truth))
                continue;
            labeled++;

            // NG recall: 정답이 NG 인 건만(ground-truth 양성) 분모에 든다.
            if (!isNg(truth))
                continue;
            labeledNg++;

            if (r.Verdict == Verdict.NG)
                vlmNgHits++;

            // ML 은 degrade 시 라벨이 null → recall 분모에서 제외(못 본 건 ≠ 틀린 건).
            if (r.MlLabel is not null)
            {
                mlNgScored++;
                if (isNg(r.MlLabel))
                    mlNgHits++;
            }
        }

        int nonDegraded = mlChecked - degraded;
        var vlmRecall = Rate(vlmNgHits, labeledNg);
        var mlRecall = Rate(mlNgHits, mlNgScored);
        var agreementRate = Rate(agreements, nonDegraded);

        // 격상 자격 신호(A1): 표본 충분 + 조건 전부 충족 → true; 표본 충분·계산 가능·조건 미달 → false; 데이터 불충분 → null.
        bool? eligible = (mlChecked >= options.MinWindow
            && vlmRecall is { } vr && mlRecall is { } mr && agreementRate is { } ar
            && mr >= vr && mr >= options.RecallFloor && ar >= options.AgreementFloor)
            ? true
            : (mlChecked >= options.MinWindow && vlmRecall is not null && mlRecall is not null && agreementRate is not null
                ? false      // 표본 충분·계산 가능하나 조건 미달 → 명시 false
                : null);     // 데이터 불충분 → null(위장 금지)

        return new MetricsSummary
        {
            ScenarioId = scenarioId,
            Date = date,
            Inspections = inspections,
            MlChecked = mlChecked,
            MatchMismatches = matchMismatches,
            MatchAbstained = matchAbstained,
            Contested = contested,
            MlDegraded = degraded,
            Agreements = agreements,
            ReviewsRequired = reviews,
            Audited = audited,
            LabelConsistent = labelConsistent,
            LabelConflictsOpen = labelConflictsOpen,
            OracleEvaluated = oracleEvaluated,
            OracleConflicts = oracleConflicts,
            OraclePending = oraclePending,
            Labeled = labeled,
            LabeledNg = labeledNg,
            VlmNgHits = vlmNgHits,
            MlNgScored = mlNgScored,
            MlNgHits = mlNgHits,
            FailClosed = failClosed,
            FailClosedRate = Rate(failClosed, inspections + failClosed),
            AgreementRate = agreementRate,
            ReviewRate = Rate(reviews, nonDegraded),
            DegradeRate = Rate(degraded, mlChecked),
            VlmNgRecall = vlmRecall,
            MlNgRecall = mlRecall,
            LabelConsistencyRate = Rate(labelConsistent, audited),
            OracleConsistencyRate = Rate(oracleEvaluated - oracleConflicts, oracleEvaluated),
            OracleCoverageRate = Rate(reviews - oraclePending, reviews),
            PromotionEligible = eligible,
        };
    }

    private static bool IsBinaryNg(string label) =>
        string.Equals(label, NgLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>분모 0 이면 null(undefined 를 0.0 으로 위장하지 않음 — 정직성).</summary>
    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;
}
