using UVision.Api.Models;

namespace UVision.Api.Services.Metrics;

/// <summary>
/// 메트릭 row + 사람 라벨을 시나리오·날짜 집계로 환원하는 순수 함수(B3 read).
/// 병렬 호출·저장소 I/O 는 엔드포인트가 소유하고, 여기서는 카운팅/비율 산출만 한다
/// (<see cref="Services.DualCheck.DualCheckEvaluator"/> 와 동일한 순수-로직 분리 규율 — 결정론·테스트 용이).
/// </summary>
public static class MetricsAggregator
{
    private const string NgLabel = "NG";

    /// <param name="rows">해당 시나리오·날짜의 메트릭 row(예측 신호).</param>
    /// <param name="labels">같은 날짜 버킷의 사람 라벨(정답) — image_id 로 조인.</param>
    public static MetricsSummary Summarize(
        string scenarioId, string date,
        IReadOnlyList<MetricsRow> rows, IReadOnlyList<StoredLabel> labels)
    {
        // image_id → 사람 라벨(정답). 마지막 값 우선(라벨은 last-write-wins 사이드카).
        var labelOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in labels)
            labelOf[l.ImageId] = l.Label;

        int failClosed = 0;
        int inspections = 0;  // 비-fail-closed (rows.Count 대신)
        int degraded = 0, agreements = 0, reviews = 0;
        int labeled = 0, labeledNg = 0, vlmNgHits = 0, mlNgScored = 0, mlNgHits = 0;

        foreach (var r in rows)
        {
            if (r.Posture == "fail_closed")
            {
                failClosed++;
                continue;  // verdict 없는 행 — 기존 집계 제외
            }
            inspections++;

            if (r.MlDegraded)
                degraded++;
            else
            {
                if (r.Agreement == true) agreements++;
                if (r.RequiresReview == true) reviews++;
            }

            if (!labelOf.TryGetValue(r.ImageId, out var truth))
                continue;
            labeled++;

            // NG recall: 정답이 NG 인 건만(ground-truth 양성) 분모에 든다.
            if (!IsNg(truth))
                continue;
            labeledNg++;

            if (r.Verdict == Verdict.NG)
                vlmNgHits++;

            // ML 은 degrade 시 라벨이 null → recall 분모에서 제외(못 본 건 ≠ 틀린 건).
            if (r.MlLabel is not null)
            {
                mlNgScored++;
                if (IsNg(r.MlLabel))
                    mlNgHits++;
            }
        }

        int nonDegraded = inspections - degraded;
        return new MetricsSummary
        {
            ScenarioId = scenarioId,
            Date = date,
            Inspections = inspections,
            MlDegraded = degraded,
            Agreements = agreements,
            ReviewsRequired = reviews,
            Labeled = labeled,
            LabeledNg = labeledNg,
            VlmNgHits = vlmNgHits,
            MlNgScored = mlNgScored,
            MlNgHits = mlNgHits,
            FailClosed = failClosed,
            FailClosedRate = Rate(failClosed, inspections + failClosed),
            AgreementRate = Rate(agreements, nonDegraded),
            ReviewRate = Rate(reviews, nonDegraded),
            DegradeRate = Rate(degraded, inspections),
            VlmNgRecall = Rate(vlmNgHits, labeledNg),
            MlNgRecall = Rate(mlNgHits, mlNgScored),
        };
    }

    private static bool IsNg(string label) =>
        string.Equals(label, NgLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>분모 0 이면 null(undefined 를 0.0 으로 위장하지 않음 — 정직성).</summary>
    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;
}
