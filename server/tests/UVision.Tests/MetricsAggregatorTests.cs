using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Metrics;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 메트릭 집계 순수 함수 단위 테스트(B3) — 카운트·비율·NG recall 라벨조인·degrade 제외·0 분모 null.
/// </summary>
public class MetricsAggregatorTests
{
    private static MetricsRow Row(
        string imageId, Verdict verdict, string? mlLabel, bool agreement, bool requiresReview,
        bool degraded = false) => new()
        {
            ImageId = imageId,
            Timestamp = "2026-06-22T12:00:00.0000000+00:00",
            Verdict = verdict,
            VlmConfidence = 0.9,
            MlLabel = mlLabel,
            MlConfidence = mlLabel is null ? null : 0.9,
            Agreement = degraded ? null : agreement,
            RequiresReview = degraded ? null : requiresReview,
            MlDegraded = degraded,
        };

    private static StoredLabel Label(string imageId, string label) => new()
    {
        ImageId = imageId,
        Label = label,
        Timestamp = "2026-06-22T13:00:00.0000000+00:00",
    };

    [Fact]
    public void Empty_YieldsZeroCounts_NullRates()
    {
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], []);

        Assert.Equal(0, s.Inspections);
        Assert.Null(s.AgreementRate);
        Assert.Null(s.ReviewRate);
        Assert.Null(s.DegradeRate);
        Assert.Null(s.VlmNgRecall);
        Assert.Null(s.MlNgRecall);
    }

    [Fact]
    public void Counts_AgreementsReviewsDegrade()
    {
        var rows = new[]
        {
            Row("a", Verdict.NG, "ng", agreement: true, requiresReview: false),
            Row("b", Verdict.OK, "ng", agreement: false, requiresReview: true),
            Row("c", Verdict.OK, null, agreement: false, requiresReview: false, degraded: true),
        };

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, []);

        Assert.Equal(3, s.Inspections);
        Assert.Equal(1, s.MlDegraded);
        Assert.Equal(1, s.Agreements);
        Assert.Equal(1, s.ReviewsRequired);
        // 비율 분모 = 비-degrade(2건).
        Assert.Equal(0.5, s.AgreementRate);
        Assert.Equal(0.5, s.ReviewRate);
        Assert.Equal(1.0 / 3.0, s.DegradeRate);
    }

    [Fact]
    public void NgRecall_JoinsLabels_VlmAndMl()
    {
        // 정답 NG 3건: a(VLM NG·ML NG), b(VLM OK 놓침·ML NG), c(VLM NG·ML OK 놓침).
        // 정답 OK 1건: d — recall 분모에 안 듦.
        var rows = new[]
        {
            Row("a", Verdict.NG, "ng", true, false),
            Row("b", Verdict.OK, "ng", false, true),
            Row("c", Verdict.NG, "ok", false, true),
            Row("d", Verdict.OK, "ok", true, false),
        };
        var labels = new[]
        {
            Label("a", "NG"), Label("b", "NG"), Label("c", "NG"), Label("d", "OK"),
        };

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels);

        Assert.Equal(4, s.Labeled);
        Assert.Equal(3, s.LabeledNg);
        Assert.Equal(2, s.VlmNgHits);   // a, c
        Assert.Equal(3, s.MlNgScored);  // a, b, c 모두 ml 라벨 존재
        Assert.Equal(2, s.MlNgHits);    // a, b
        Assert.Equal(2.0 / 3.0, s.VlmNgRecall);
        Assert.Equal(2.0 / 3.0, s.MlNgRecall);
    }

    [Fact]
    public void NgRecall_DegradedMl_ExcludedFromMlDenominator()
    {
        // 정답 NG 2건. 하나는 ML degrade(라벨 null) → ML recall 분모 제외(못 본 건 ≠ 틀린 건).
        var rows = new[]
        {
            Row("a", Verdict.NG, "ng", true, false),
            Row("b", Verdict.NG, null, false, false, degraded: true),
        };
        var labels = new[] { Label("a", "NG"), Label("b", "NG") };

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels);

        Assert.Equal(2, s.LabeledNg);
        Assert.Equal(2, s.VlmNgHits);   // VLM 은 둘 다 NG
        Assert.Equal(1.0, s.VlmNgRecall);
        Assert.Equal(1, s.MlNgScored);  // a 만(b 는 degrade)
        Assert.Equal(1, s.MlNgHits);
        Assert.Equal(1.0, s.MlNgRecall);
    }

    [Fact]
    public void NgRecall_NoLabeledNg_NullRecall()
    {
        var rows = new[] { Row("a", Verdict.NG, "ng", true, false) };
        var labels = new[] { Label("a", "OK") }; // 정답이 OK 뿐 → NG recall 분모 0.

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels);

        Assert.Equal(1, s.Labeled);
        Assert.Equal(0, s.LabeledNg);
        Assert.Null(s.VlmNgRecall);
        Assert.Null(s.MlNgRecall);
    }

    [Fact]
    public void LabelMatch_IsCaseInsensitive()
    {
        // 라벨 "ng"(소문자)·ML "NG"(대문자)라도 일치 비교(DualCheckEvaluator 규약과 동일).
        var rows = new[] { Row("a", Verdict.NG, "NG", true, false) };
        var labels = new[] { Label("a", "ng") };

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels);

        Assert.Equal(1, s.LabeledNg);
        Assert.Equal(1.0, s.VlmNgRecall);
        Assert.Equal(1.0, s.MlNgRecall);
    }

    [Fact]
    public void UnlabeledRows_NotCountedAsLabeled()
    {
        var rows = new[]
        {
            Row("a", Verdict.NG, "ng", true, false),
            Row("b", Verdict.NG, "ng", true, false),
        };
        var labels = new[] { Label("a", "NG") }; // b 는 미라벨.

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels);

        Assert.Equal(2, s.Inspections);
        Assert.Equal(1, s.Labeled);
        Assert.Equal(1, s.LabeledNg);
    }

    // --- fail-closed posture 집계 (③.5 E2) ---

    private static MetricsRow FailClosedRow(string id) => new()
    {
        ImageId = id, Timestamp = "t", Verdict = null, VlmConfidence = null,
        Posture = MetricsRow.PostureFailClosed, MlDegraded = false,
    };

    private static MetricsRow OkRow(string id) => new()
    {
        ImageId = id, Timestamp = "t", Verdict = Verdict.OK, VlmConfidence = 0.9,
        MlLabel = "ok", MlConfidence = 0.9, Agreement = true, RequiresReview = false,
        MlDegraded = false,
    };

    [Fact]
    public void FailClosed_CountedSeparately_InspectionsExcludeThem()
    {
        var rows = new[] { OkRow("a"), OkRow("b"), FailClosedRow("c") };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, []);

        Assert.Equal(2, s.Inspections);      // 비-fail-closed 만
        Assert.Equal(1, s.FailClosed);
        Assert.Equal(2, s.Agreements);       // fail-closed 는 agreement 집계 제외
        Assert.Equal(1.0 / 3.0, s.FailClosedRate); // 1 / (2+1) 총 시도
    }

    [Fact]
    public void FailClosedRate_Null_WhenNoAttempts()
    {
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], []);
        Assert.Equal(0, s.FailClosed);
        Assert.Null(s.FailClosedRate);
    }

    [Fact]
    public void LegacyRow_NullPosture_TreatedAsNonFailClosed()
    {
        // posture 없는 구 행(OkRow 는 Posture 미설정 → null) 은 inspections 에 포함.
        var rows = new[] { OkRow("a") };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, []);
        Assert.Equal(1, s.Inspections);
        Assert.Equal(0, s.FailClosed);
    }

    // --- 라벨 일관성 집계 (C1) ---

    private static StoredLabel Labeled(string id, string label, string? auditStatus) => new()
    {
        ImageId = id, Label = label, Timestamp = "t",
        Audit = new LabelAudit { Status = auditStatus ?? LabelAuditStatus.Unaudited },
    };

    [Fact]
    public void Consistency_CountsAuditedAndRate_FlagOnly()
    {
        var labels = new[]
        {
            Labeled("a", "NG", LabelAuditStatus.Consistent),
            Labeled("b", "NG", LabelAuditStatus.Conflicted),
            Labeled("c", "OK", LabelAuditStatus.Resolved),
            Labeled("d", "OK", LabelAuditStatus.Unaudited), // 미감사 — audited 제외
        };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], labels);

        Assert.Equal(3, s.Audited);              // consistent+conflicted+resolved
        Assert.Equal(1, s.LabelConsistent);
        Assert.Equal(1, s.LabelConflictsOpen);   // 미해소 충돌만
        Assert.Equal(1.0 / 3.0, s.LabelConsistencyRate); // 1 / 3
    }

    [Fact]
    public void ConsistencyRate_Null_WhenNothingAudited()
    {
        var labels = new[] { Labeled("a", "NG", LabelAuditStatus.Unaudited) };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], labels);
        Assert.Equal(0, s.Audited);
        Assert.Null(s.LabelConsistencyRate); // 분모 0 → null(0% 위장 금지)
    }
}
