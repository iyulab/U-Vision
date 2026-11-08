using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Authority;
using UVision.Api.Services.Label;
using Xunit;

namespace UVision.Tests;

public class AutoDemoteEvaluatorTests
{
    private static MetricsSummary Summary(int inspections, double? mlRecall, double? vlmRecall) => new()
    {
        ScenarioId = "demo", Date = "2026-06-23",
        Inspections = inspections, MlChecked = inspections, MatchMismatches = 0, MatchAbstained = 0, Contested = 0,
        MlDegraded = 0, FailClosed = 0, Agreements = 0, ReviewsRequired = 0,
        Audited = 0, LabelConsistent = 0, LabelConflictsOpen = 0,
        OracleEvaluated = 0, OracleConflicts = 0, OraclePending = 0,
        Labeled = inspections, LabeledNg = inspections, VlmNgHits = 0, MlNgScored = inspections, MlNgHits = 0,
        MlNgRecall = mlRecall, VlmNgRecall = vlmRecall,
    };

    [Fact]
    public void CoPrimary_MlBelowVlm_DemotesToAdvisory()
    {
        var t = AutoDemoteEvaluator.Evaluate(AuthorityStage.CoPrimary,
            Summary(60, mlRecall: 0.8, vlmRecall: 0.95), new AuthorityOptions());
        Assert.Equal(AuthorityStage.Advisory, t);
    }

    [Fact]
    public void MlPrimary_MlBelowFloor_DemotesToCoPrimary()
    {
        var t = AutoDemoteEvaluator.Evaluate(AuthorityStage.MlPrimary,
            Summary(60, mlRecall: 0.9, vlmRecall: 0.85), new AuthorityOptions()); // 0.9 < RecallFloor 0.95
        Assert.Equal(AuthorityStage.CoPrimary, t);
    }

    [Fact]
    public void CoPrimary_Healthy_NoDemote()
    {
        var t = AutoDemoteEvaluator.Evaluate(AuthorityStage.CoPrimary,
            Summary(60, mlRecall: 0.97, vlmRecall: 0.95), new AuthorityOptions());
        Assert.Null(t);
    }

    [Fact]
    public void SmallWindow_NoDemote() =>
        Assert.Null(AutoDemoteEvaluator.Evaluate(AuthorityStage.MlPrimary,
            Summary(10, mlRecall: 0.5, vlmRecall: 0.95), new AuthorityOptions()));

    [Fact]
    public void Advisory_NeverDemotes() =>
        Assert.Null(AutoDemoteEvaluator.Evaluate(AuthorityStage.Advisory,
            Summary(60, mlRecall: 0.1, vlmRecall: 0.95), new AuthorityOptions()));

    [Fact]
    public void NullRecall_NoDemote() =>
        Assert.Null(AutoDemoteEvaluator.Evaluate(AuthorityStage.CoPrimary,
            Summary(60, mlRecall: null, vlmRecall: 0.95), new AuthorityOptions()));

    // ── 같은 날짜 버킷 안에서 모델 판이 바뀐 경우 ──────────────────────────────────────────────────────────
    private const string Old = "sha-old";
    private const string New = "sha-new";

    private static void Add(List<MetricsRow> rows, List<StoredLabel> labels, string id, bool ng, string? model, string mlLabel,
        bool vlmOnly = false, bool degraded = false)
    {
        rows.Add(new MetricsRow
        {
            ImageId = id, Timestamp = "t", Verdict = Verdict.OK, // VLM 은 불량을 놓친다 — ML 이 더 낫다는 비교가 자명하도록
            MlLabel = degraded || vlmOnly ? null : mlLabel, MlConfidence = degraded || vlmOnly ? null : 0.9, MlModel = model,
            Agreement = degraded || vlmOnly ? null : true, MlDegraded = degraded, VlmOnly = vlmOnly,
        });
        labels.Add(new StoredLabel { ImageId = id, Label = ng ? "NG" : "OK", Timestamp = "t" });
    }

    // 옛 좋은 판이 불량 100건을 모두 잡은 뒤, 같은 날 새 판이 불량 60건을 모두 놓쳤다.
    private static (List<MetricsRow> Rows, List<StoredLabel> Labels) GoodOldThenBadNew()
    {
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 100; i++) Add(rows, labels, $"oldng{i}", true, Old, "NG");
        for (int i = 0; i < 60; i++) Add(rows, labels, $"newng{i}", true, New, "OK");
        return (rows, labels);
    }

    [Fact]
    public void AGoodOldModel_HidesABadNewModel_WhenTheWindowIsMixed()
    {
        // 결함의 재현: 판이 섞인 버킷 전체로 재면 옛 판 1200건 적중이 새 판 60건 놓침을 가려 ML 재현율이 0.952 로 floor(0.95)를 넘는다.
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 1200; i++) Add(rows, labels, $"oldng{i}", true, Old, "NG");
        for (int i = 0; i < 60; i++) Add(rows, labels, $"newng{i}", true, New, "OK");

        var options = new AuthorityOptions();
        var mixed = UVision.Api.Services.Metrics.MetricsAggregator.Summarize("demo", "2026-11-08", rows, labels, options);
        Assert.Null(AutoDemoteEvaluator.Evaluate(AuthorityStage.CoPrimary, mixed, options)); // 1200/1260 = 0.952 ≥ floor · ≥ VLM 0 → 격하 안 함(위험)

        var current = UVision.Api.Services.Metrics.MetricsAggregator.Summarize(
            "demo", "2026-11-08", AutoDemoteEvaluator.CurrentModelRows(rows), labels, options);
        Assert.Equal(AuthorityStage.Advisory, AutoDemoteEvaluator.Evaluate(AuthorityStage.CoPrimary, current, options)); // 새 판만: 0/60 → 격하
    }

    [Fact]
    public void CurrentModelRows_KeepsOnlyTheLatestModelsRows()
    {
        var (rows, _) = GoodOldThenBadNew();
        var kept = AutoDemoteEvaluator.CurrentModelRows(rows);
        Assert.Equal(60, kept.Count);
        Assert.All(kept, r => Assert.Equal(New, r.MlModel));
    }

    [Fact]
    public void CurrentModelRows_WithoutAnyModelIdentity_ReturnsTheWindowUnchanged()
    {
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 20; i++) Add(rows, labels, $"a{i}", true, null, "NG");
        Assert.Same(rows, AutoDemoteEvaluator.CurrentModelRows(rows)); // 신원을 안 싣는 서버 · mock — 이전과 같은 판정
    }

    [Fact]
    public void CurrentModelRows_DropsUnidentifiedDegradedRows_ButKeepsVlmOnlyRows()
    {
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        Add(rows, labels, "old", true, Old, "NG");
        Add(rows, labels, "deg", true, null, "NG", degraded: true);
        Add(rows, labels, "vlm", true, null, "NG", vlmOnly: true);
        Add(rows, labels, "new", true, New, "NG");
        var kept = AutoDemoteEvaluator.CurrentModelRows(rows);
        Assert.Equal(new[] { "vlm", "new" }, kept.Select(r => r.ImageId).ToArray());
    }
}
