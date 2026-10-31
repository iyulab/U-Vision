using UVision.Api.Configuration;
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
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], [], new AuthorityOptions());

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

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, [], new AuthorityOptions());

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

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels, new AuthorityOptions());

        Assert.Equal(4, s.Labeled);
        Assert.Equal(3, s.LabeledNg);
        Assert.Equal(2, s.VlmNgHits);   // a, c
        Assert.Equal(3, s.MlNgScored);  // a, b, c 모두 ml 라벨 존재
        Assert.Equal(2, s.MlNgHits);    // a, b
        Assert.Equal(2.0 / 3.0, s.VlmNgRecall);
        Assert.Equal(2.0 / 3.0, s.MlNgRecall);
    }

    [Fact]
    public void NgRecall_DefectTypes_AllCountAsNg()
    {
        // 유형 시나리오: 정답 scratch(a) · dent(b) · OK(c). ML 이 a 를 dent 로 봐도 «불량을 불량으로» 잡은 것이다.
        var rows = new[]
        {
            Row("a", Verdict.NG, "dent", false, true),
            Row("b", Verdict.OK, "OK", false, true),
            Row("c", Verdict.OK, "OK", true, false),
        };
        var labels = new[] { Label("a", "scratch"), Label("b", "dent"), Label("c", "OK") };
        var typed = UVision.Api.Services.Scenarios.EffectiveScenario.Resolve(new Scenario
        {
            ScenarioId = "t", Name = "t",
            Classes = [new() { Id = "OK", Settleable = true }, new() { Id = "scratch", MustCatch = true }, new() { Id = "dent", MustCatch = true }],
        }, mlEnabled: true);

        var s = MetricsAggregator.Summarize("t", "2026-06-22", rows, labels, new AuthorityOptions(), typed.NgLabelRule);

        Assert.Equal(2, s.LabeledNg);
        Assert.Equal(1, s.VlmNgHits);
        Assert.Equal(1, s.MlNgHits);
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

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels, new AuthorityOptions());

        Assert.Equal(2, s.LabeledNg);
        Assert.Equal(2, s.VlmNgHits);   // VLM 은 둘 다 NG
        Assert.Equal(1.0, s.VlmNgRecall);
        Assert.Equal(1, s.MlNgScored);  // a 만(b 는 degrade)
        Assert.Equal(1, s.MlNgHits);
        Assert.Equal(1.0, s.MlNgRecall);
    }

    private static StoredLabel LearnLabel(string imageId, string label, string auditStatus = LabelAuditStatus.Unaudited) => new()
    {
        ImageId = imageId,
        Label = label,
        Timestamp = "2026-06-22T13:00:00.0000000+00:00",
        History = [new LabelEvent { Label = label, By = "dev", At = "2026-06-22T13:00:00.0000000+00:00", Mode = LabelMode.Label, Stream = LabelStream.Learn }],
        Audit = new LabelAudit { Status = auditStatus },
    };

    [Fact]
    public void LearnStreamLabels_AreNotReadByRecallOrAuditCounts()
    {
        // 측정용 NG 2건(a, b)과 학습용 NG 2건(c, d) — 학습용은 어려운 사례가 농축된 표본이라 재현율의 분모에 들면 안 된다.
        var rows = new[]
        {
            Row("a", Verdict.NG, "ng", true, false),
            Row("b", Verdict.NG, "ng", true, false),
            Row("c", Verdict.OK, "ok", true, false),
            Row("d", Verdict.OK, "ok", true, false),
        };
        var labels = new[]
        {
            Label("a", "NG"), Label("b", "NG"),
            LearnLabel("c", "NG", LabelAuditStatus.Conflicted), LearnLabel("d", "NG", LabelAuditStatus.Consistent),
        };

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels, new AuthorityOptions());

        Assert.Equal(2, s.Labeled);
        Assert.Equal(2, s.LabeledNg);
        Assert.Equal(1.0, s.VlmNgRecall);       // 학습용 c, d 의 놓침이 섞였다면 0.5
        Assert.Equal(0, s.LabelConflictsOpen);  // 학습용 라벨의 감사 상태는 일관성 집계에 안 든다
        Assert.Equal(0, s.LabelConsistent);
    }

    [Fact]
    public void LabelWithoutStream_IsReadAsMeasure()
    {
        // 표식이 없는 라벨(이전 사이드카 · 운영 화면의 라벨)은 측정용으로 읽힌다 — 기존 동작 보존.
        var rows = new[] { Row("a", Verdict.NG, "ng", true, false) };

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, [Label("a", "NG")], new AuthorityOptions());

        Assert.Equal(1, s.LabeledNg);
    }

    [Fact]
    public void NgRecall_NoLabeledNg_NullRecall()
    {
        var rows = new[] { Row("a", Verdict.NG, "ng", true, false) };
        var labels = new[] { Label("a", "OK") }; // 정답이 OK 뿐 → NG recall 분모 0.

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels, new AuthorityOptions());

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

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels, new AuthorityOptions());

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

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels, new AuthorityOptions());

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
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, [], new AuthorityOptions());

        Assert.Equal(2, s.Inspections);      // 비-fail-closed 만
        Assert.Equal(1, s.FailClosed);
        Assert.Equal(2, s.Agreements);       // fail-closed 는 agreement 집계 제외
        Assert.Equal(1.0 / 3.0, s.FailClosedRate); // 1 / (2+1) 총 시도
    }

    [Fact]
    public void FailClosedRate_Null_WhenNoAttempts()
    {
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], [], new AuthorityOptions());
        Assert.Equal(0, s.FailClosed);
        Assert.Null(s.FailClosedRate);
    }

    [Fact]
    public void LegacyRow_NullPosture_TreatedAsNonFailClosed()
    {
        // posture 없는 구 행(OkRow 는 Posture 미설정 → null) 은 inspections 에 포함.
        var rows = new[] { OkRow("a") };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, [], new AuthorityOptions());
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
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], labels, new AuthorityOptions());

        Assert.Equal(3, s.Audited);              // consistent+conflicted+resolved
        Assert.Equal(1, s.LabelConsistent);
        Assert.Equal(1, s.LabelConflictsOpen);   // 미해소 충돌만
        Assert.Equal(1.0 / 3.0, s.LabelConsistencyRate); // 1 / 3
    }

    [Fact]
    public void ConsistencyRate_Null_WhenNothingAudited()
    {
        var labels = new[] { Labeled("a", "NG", LabelAuditStatus.Unaudited) };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], labels, new AuthorityOptions());
        Assert.Equal(0, s.Audited);
        Assert.Null(s.LabelConsistencyRate); // 분모 0 → null(0% 위장 금지)
    }

    // --- 오라클↔사람 불일치 집계 (④-B 후속 — 계통편향 flag-only) ---

    private static StoredLabel WithOracle(string id, string? operativeLabel, string oracleOpinion)
    {
        var history = new List<LabelEvent>();
        if (operativeLabel is not null)
            history.Add(new LabelEvent { Label = operativeLabel, By = "dev", At = "t1", Mode = LabelMode.Label });
        history.Add(new LabelEvent { Label = oracleOpinion, By = "oracle", At = "t2", Mode = LabelMode.Oracle });
        return new StoredLabel { ImageId = id, Label = operativeLabel, Timestamp = "t", History = history };
    }

    [Fact]
    public void Oracle_CountsConflictsAndRate_FlagOnly()
    {
        // 계통편향 신호: 오라클(독립 2차의견)이 사람 operative 라벨과 얼마나 불일치하는가.
        var labels = new[]
        {
            WithOracle("a", "OK", "OK"),  // 일치
            WithOracle("b", "OK", "NG"),  // 불일치 — 사람 누락 의심
            WithOracle("c", "NG", "ng"),  // 일치(대소문자 무시 — 코드베이스 규약)
        };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], labels, new AuthorityOptions());

        Assert.Equal(3, s.OracleEvaluated);
        Assert.Equal(1, s.OracleConflicts);
        Assert.Equal(2.0 / 3.0, s.OracleConsistencyRate);
    }

    [Fact]
    public void Oracle_OnlyOpinion_NoOperative_NotEvaluated()
    {
        // oracle-only(사람 미라벨) — operative 없으니 비교 불가(분모 제외).
        var labels = new[] { WithOracle("a", null, "NG") };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], labels, new AuthorityOptions());

        Assert.Equal(0, s.OracleEvaluated);
        Assert.Null(s.OracleConsistencyRate); // 분모 0 → null(0% 위장 금지)
    }

    [Fact]
    public void Oracle_NoOpinion_NotEvaluated()
    {
        // 사람 라벨만, 오라클 소견 없음 — 비교 대상 아님.
        var labels = new[] { Label("a", "NG") };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], labels, new AuthorityOptions());

        Assert.Equal(0, s.OracleEvaluated);
        Assert.Equal(0, s.OracleConflicts);
        Assert.Null(s.OracleConsistencyRate);
    }

    // --- 오라클 스윕 커버리지/백로그 (④-B 후속 — 호출율 축) ---

    [Fact]
    public void Oracle_Coverage_PendingAndRate()
    {
        // requires_review 3건: a 오라클 처리됨(소견 존재), b·c 미처리(스윕 백로그). d 는 검토 아님.
        var rows = new[]
        {
            Row("a", Verdict.NG, "ok", agreement: false, requiresReview: true),
            Row("b", Verdict.NG, "ok", agreement: false, requiresReview: true),
            Row("c", Verdict.NG, "ok", agreement: false, requiresReview: true),
            Row("d", Verdict.OK, "ok", agreement: true, requiresReview: false),
        };
        var labels = new[] { WithOracle("a", null, "NG") }; // a 만 오라클 소견 있음
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels, new AuthorityOptions());

        Assert.Equal(3, s.ReviewsRequired);
        Assert.Equal(2, s.OraclePending);              // b, c (미처리 백로그)
        Assert.Equal(1.0 / 3.0, s.OracleCoverageRate); // 1 covered / 3 review
    }

    [Fact]
    public void Oracle_Coverage_NullRate_WhenNoReviews()
    {
        var rows = new[] { Row("a", Verdict.OK, "ok", true, false) }; // 검토 0건
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, [], new AuthorityOptions());

        Assert.Equal(0, s.OraclePending);
        Assert.Null(s.OracleCoverageRate); // 분모 0 → null
    }

    [Fact]
    public void Oracle_Coverage_DegradedReview_NotCounted()
    {
        // degrade 행은 requires_review=null → 검토/커버리지 분모 제외(못 본 건 ≠ 검토 대상).
        var rows = new[] { Row("a", Verdict.NG, null, false, false, degraded: true) };
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, [], new AuthorityOptions());

        Assert.Equal(0, s.ReviewsRequired);
        Assert.Equal(0, s.OraclePending);
        Assert.Null(s.OracleCoverageRate);
    }

    // --- 격상 자격 신호(A1) ---

    [Fact]
    public void PromotionEligible_TrueWhenAllConditionsMet()
    {
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 60; i++)
        {
            var id = $"img{i}";
            // 모두 NG 라벨, VLM·ML 둘 다 NG 적중, 일치 → ml_ng_recall=1, vlm=1, agreement=1
            rows.Add(new MetricsRow { ImageId = id, Timestamp = "t", Verdict = Verdict.NG,
                MlLabel = "NG", MlConfidence = 0.9, Agreement = true, RequiresReview = false, MlDegraded = false });
            labels.Add(new StoredLabel { ImageId = id, Label = "NG", Timestamp = "t" });
        }
        var s = MetricsAggregator.Summarize("demo", "2026-06-23", rows, labels, new AuthorityOptions());
        Assert.True(s.PromotionEligible);
    }

    [Fact]
    public void PromotionEligible_FalseWhenWindowMet_ButConditionsUnmet()
    {
        // 표본 충분·계산 가능하나 ML recall 이 VLM 미만 → 명시 false(null 아님 — 데이터는 있는데 조건 미달).
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 60; i++)
        {
            var id = $"img{i}";
            // 정답 NG. VLM 은 NG 적중(recall 1.0), ML 은 OK 놓침(recall 0.0) → ml < vlm.
            rows.Add(new MetricsRow { ImageId = id, Timestamp = "t", Verdict = Verdict.NG,
                MlLabel = "OK", MlConfidence = 0.9, Agreement = false, RequiresReview = true, MlDegraded = false });
            labels.Add(new StoredLabel { ImageId = id, Label = "NG", Timestamp = "t" });
        }
        var s = MetricsAggregator.Summarize("demo", "2026-06-23", rows, labels, new AuthorityOptions());
        Assert.False(s.PromotionEligible);  // null 아님 — 표본 충분(60≥50)·계산 가능·조건 미달
        Assert.Equal(0.0, s.MlNgRecall);
        Assert.Equal(1.0, s.VlmNgRecall);
    }

    [Fact]
    public void FalseAlarms_CountOnlyHumanOkLabels_ForVlmAndMl()
    {
        // 정답 OK 4건: VLM 이 NG 로 부른 것 1건, ML 이 NG 로 부른 것 2건, ML degrade 1건(분모에서 제외).
        var rows = new List<MetricsRow>
        {
            Row("a", Verdict.OK, "OK", agreement: true, requiresReview: false),
            Row("b", Verdict.NG, "NG", agreement: true, requiresReview: false),
            Row("c", Verdict.OK, "NG", agreement: false, requiresReview: true),
            Row("d", Verdict.OK, null, agreement: true, requiresReview: false, degraded: true),
            Row("e", Verdict.NG, "NG", agreement: true, requiresReview: false),   // 정답 NG — 오경보가 아니다
        };
        var labels = new List<StoredLabel> { Label("a", "OK"), Label("b", "OK"), Label("c", "OK"), Label("d", "OK"), Label("e", "NG") };

        var s = MetricsAggregator.Summarize("demo", "2026-06-22", rows, labels, new AuthorityOptions());

        Assert.Equal(4, s.LabeledOk);
        Assert.Equal(1, s.VlmFalseAlarms);
        Assert.Equal(0.25, s.VlmFalseAlarmRate);
        Assert.Equal(3, s.MlOkScored);
        Assert.Equal(2, s.MlFalseAlarms);
        Assert.Equal(2.0 / 3.0, s.MlFalseAlarmRate!.Value, 9);
    }

    [Fact]
    public void FalseAlarmRates_AreNullWithoutHumanOkLabels()
    {
        var s = MetricsAggregator.Summarize("demo", "2026-06-22", [], [], new AuthorityOptions());

        Assert.Null(s.VlmFalseAlarmRate);
        Assert.Null(s.MlFalseAlarmRate);
        Assert.Equal(0, s.LabeledOk);
    }

    [Fact]
    public void PromotionEligible_NullWhenNgSamplesTooFew_EvenIfWindowMet()
    {
        // 검사 60건이지만 사람 라벨 NG 는 1건 — ML 이 맞히고 VLM 이 놓쳐 점추정은 1.0 대 0.0 이어도 true 가 아니라 null.
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 60; i++)
        {
            var id = $"img{i}";
            bool ng = i == 0;
            rows.Add(new MetricsRow { ImageId = id, Timestamp = "t", Verdict = Verdict.OK, // VLM 은 놓침
                MlLabel = ng ? "NG" : "OK", MlConfidence = 0.9, Agreement = !ng, RequiresReview = ng, MlDegraded = false });
            labels.Add(new StoredLabel { ImageId = id, Label = ng ? "NG" : "OK", Timestamp = "t" });
        }
        var s = MetricsAggregator.Summarize("demo", "2026-06-23", rows, labels, new AuthorityOptions());
        Assert.Equal(1, s.LabeledNg);
        Assert.Equal(1.0, s.MlNgRecall);
        Assert.Null(s.PromotionEligible); // labeled_ng(1) < MinNgSamples(59) — 1/1 을 «준비됨» 으로 읽지 않는다
    }

    [Fact]
    public void PromotionEligible_NgFloorFollowsOption()
    {
        // 같은 데이터(NG 3건)라도 설치가 하한을 낮추면 판정이 난다 — 하한은 옵션이다.
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 60; i++)
        {
            var id = $"img{i}";
            bool ng = i < 3;
            rows.Add(new MetricsRow { ImageId = id, Timestamp = "t", Verdict = ng ? Verdict.NG : Verdict.OK,
                MlLabel = ng ? "NG" : "OK", MlConfidence = 0.9, Agreement = true, RequiresReview = false, MlDegraded = false });
            labels.Add(new StoredLabel { ImageId = id, Label = ng ? "NG" : "OK", Timestamp = "t" });
        }
        Assert.Null(MetricsAggregator.Summarize("demo", "2026-06-23", rows, labels, new AuthorityOptions()).PromotionEligible);
        Assert.True(MetricsAggregator.Summarize("demo", "2026-06-23", rows, labels, new AuthorityOptions { MinNgSamples = 3 }).PromotionEligible);
    }

    [Fact]
    public void PromotionEligible_NullWhenWindowTooSmall()
    {
        var rows = new List<MetricsRow>
        {
            new() { ImageId = "a", Timestamp = "t", Verdict = Verdict.NG, MlLabel = "NG",
                MlConfidence = 0.9, Agreement = true, RequiresReview = false, MlDegraded = false },
        };
        var labels = new List<StoredLabel> { new() { ImageId = "a", Label = "NG", Timestamp = "t" } };
        var s = MetricsAggregator.Summarize("demo", "2026-06-23", rows, labels, new AuthorityOptions());
        Assert.Null(s.PromotionEligible); // inspections(1) < MinWindow(50)
    }
}
