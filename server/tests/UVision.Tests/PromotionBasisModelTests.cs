using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Metrics;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 격상 신호의 ML 쪽 항은 창 전체가 아니라 «가장 최근에 답한 모델 판» 의 행으로 잰다(이슈 초안 promotion-signal-mixes-model-versions).
/// 재생(SMT 역삽 긴 일정)에서 신호가 옛 약한 모델의 점수에 눌려 늦게 따라오는 것을 봤고, 같은 구조라면 옛 좋은 모델의 점수가
/// 나빠진 새 모델을 가릴 수 있다 — 이 시험이 그 위험한 쪽을 지킨다.
/// </summary>
public class PromotionBasisModelTests
{
    private const string Old = "sha-old";
    private const string New = "sha-new";

    // 사람 라벨된 한 장. VLM 은 불량을 놓친다(Verdict.OK) — ML 이 더 낫다는 비교 항이 자명하게 통과하도록.
    private static void Add(List<MetricsRow> rows, List<StoredLabel> labels, string id, bool ng, string? model, string mlLabel, double score)
    {
        rows.Add(new MetricsRow
        {
            ImageId = id, Timestamp = "t", Verdict = Verdict.OK, MlLabel = mlLabel, MlConfidence = 0.9,
            MlNgScore = score, MlModel = model, Agreement = true, RequiresReview = false, MlDegraded = false,
        });
        labels.Add(new StoredLabel { ImageId = id, Label = ng ? "NG" : "OK", Timestamp = "t" });
    }

    // 옛 좋은 판이 불량 1200 · 정상 600 을 완벽히 가른 뒤, 새 판이 순위를 뒤집은 불량 60 · 정상 30 을 답했다.
    private static (List<MetricsRow> Rows, List<StoredLabel> Labels) GoodOldThenBadNew()
    {
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 1200; i++) Add(rows, labels, $"oldng{i}", true, Old, "NG", 0.9);
        for (int i = 0; i < 600; i++) Add(rows, labels, $"oldok{i}", false, Old, "OK", 0.1);
        for (int i = 0; i < 60; i++) Add(rows, labels, $"newng{i}", true, New, "OK", 0.2);   // 불량을 정상 쪽으로
        for (int i = 0; i < 30; i++) Add(rows, labels, $"newok{i}", false, New, "NG", 0.8);  // 정상을 불량 쪽으로
        return (rows, labels);
    }

    [Fact]
    public void ABadNewModel_IsNotMadeEligibleByTheGoodOldModelsScores()
    {
        var (rows, labels) = GoodOldThenBadNew();
        var s = MetricsAggregator.Summarize("demo", "2026-11-08", rows, labels, new AuthorityOptions());

        // 창 전체로 재면 합친 재현율@오경보 = 0.952 ≥ 0.95 로 «적격» 이 났을 구조 — 새 판만으로 재면 0 이라 명시 false.
        Assert.False(s.PromotionEligible);
        Assert.NotNull(s.PromotionBasis);
        Assert.Equal(New, s.PromotionBasis!.Model);
        Assert.Equal(60, s.PromotionBasis.NgScored);
        Assert.Equal(30, s.PromotionBasis.OkScored);
    }

    [Fact]
    public void TheWindowWideMlMetrics_StillMixEveryModel_AndKeepTheirValues()
    {
        var (rows, labels) = GoodOldThenBadNew();
        var s = MetricsAggregator.Summarize("demo", "2026-11-08", rows, labels, new AuthorityOptions());

        // 신호만 모델 판을 가른다 — 다른 지표의 값(창 전체)은 그대로여야 wire 값이 보존된다.
        Assert.Equal(1260, s.MlNgScored);
        Assert.Equal(630, s.MlOkScored);
        Assert.Equal(1260, s.MlRankedNg);
        Assert.Equal(630, s.MlRankedOk);
    }

    [Fact]
    public void ANewModelWithTooFewScoredDefects_IsNull_NotInheritedFromTheOldModel()
    {
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 1200; i++) Add(rows, labels, $"oldng{i}", true, Old, "NG", 0.9);
        for (int i = 0; i < 600; i++) Add(rows, labels, $"oldok{i}", false, Old, "OK", 0.1);
        // 새 판은 좋지만 점수 매긴 불량이 10 건뿐 — 하한(59)에 못 미치니 «적격 아님» 이 아니라 «아직 모름».
        for (int i = 0; i < 10; i++) Add(rows, labels, $"newng{i}", true, New, "NG", 0.9);
        for (int i = 0; i < 40; i++) Add(rows, labels, $"newok{i}", false, New, "OK", 0.1);
        var s = MetricsAggregator.Summarize("demo", "2026-11-08", rows, labels, new AuthorityOptions());

        Assert.Null(s.PromotionEligible);
        Assert.Equal(10, s.PromotionBasis!.NgScored);
    }

    [Fact]
    public void AGoodNewModel_IsNotHeldBackByAWeakOldModel()
    {
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        // 옛 판은 순위를 뒤집어 놓았고(재생에서 본 지연의 극단), 새 판은 완벽하다.
        for (int i = 0; i < 100; i++) Add(rows, labels, $"oldng{i}", true, Old, "OK", 0.2);
        for (int i = 0; i < 100; i++) Add(rows, labels, $"oldok{i}", false, Old, "NG", 0.8);
        for (int i = 0; i < 60; i++) Add(rows, labels, $"newng{i}", true, New, "NG", 0.9);
        for (int i = 0; i < 60; i++) Add(rows, labels, $"newok{i}", false, New, "OK", 0.1);
        var s = MetricsAggregator.Summarize("demo", "2026-11-08", rows, labels, new AuthorityOptions());

        Assert.True(s.PromotionEligible);
        Assert.Equal(New, s.PromotionBasis!.Model);
    }

    [Fact]
    public void RowsThatNameNoModel_BehaveExactlyAsBefore_AndReportNoBasis()
    {
        var rows = new List<MetricsRow>();
        var labels = new List<StoredLabel>();
        for (int i = 0; i < 60; i++) Add(rows, labels, $"ng{i}", true, null, "NG", 0.9);
        for (int i = 0; i < 30; i++) Add(rows, labels, $"ok{i}", false, null, "OK", 0.1);
        var s = MetricsAggregator.Summarize("demo", "2026-11-08", rows, labels, new AuthorityOptions());

        Assert.True(s.PromotionEligible);   // 모델 신원이 없는 행(옛 행 · mock)은 창 전체로 — 이전과 같은 판정
        Assert.Null(s.PromotionBasis);      // 응답에 새 필드가 생기지 않는다(byte 보존)
    }

    [Fact]
    public void ModelIdentity_IsNeverWrittenWhenAbsent()
    {
        var row = new MetricsRow { ImageId = "a", Timestamp = "t", MlDegraded = false };
        Assert.DoesNotContain("ml_model", System.Text.Json.JsonSerializer.Serialize(row));
        Assert.Contains("\"ml_model\":\"sha-x\"", System.Text.Json.JsonSerializer.Serialize(row with { MlModel = "sha-x" }));
    }
}
