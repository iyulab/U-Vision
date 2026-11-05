using System.Text.Json;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Metrics;
using UVision.Api.Services.Scenarios;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 선별(<c>classification</c>) 메트릭 — 행에 판정이 정한 클래스가 실리고(다른 행의 byte 는 그대로), 집계는 사람 라벨과 견줘 클래스별
/// 혼동 행렬 · 일치율을 내며, NG recall 은 «꼭 잡을 클래스(must_catch)» 의 recall 이다.
/// </summary>
public class ClassificationMetricsTests
{
    private static readonly ScenarioClass[] Classes =
    [
        new() { Id = "P-100" },
        new() { Id = "P-200" },
        new() { Id = "REJECT", MustCatch = true },
    ];

    private static MetricsRow Row(string imageId, string? cls, Verdict? verdict = null, string? ml = null) => new()
    {
        ImageId = imageId,
        Timestamp = "2026-11-05T12:00:00.0000000Z",
        Verdict = verdict ?? (cls is null ? Verdict.OK : ScenarioClass.ViewOf(Classes, cls)),
        VlmConfidence = 0.9,
        MlLabel = ml,
        MlConfidence = ml is null ? null : 0.9,
        Agreement = ml is null ? null : string.Equals(ml, cls, StringComparison.OrdinalIgnoreCase),
        RequiresReview = ml is null ? null : !string.Equals(ml, cls, StringComparison.OrdinalIgnoreCase),
        MlDegraded = false,
        VlmOnly = ml is null,
        Class = cls,
    };

    private static StoredLabel Label(string imageId, string label) => new()
    {
        ImageId = imageId, Label = label, Timestamp = "2026-11-05T13:00:00.0000000Z",
    };

    private static MetricsSummary Summarize(MetricsRow[] rows, StoredLabel[] labels)
    {
        var effective = EffectiveScenario.Resolve(
            new Scenario { ScenarioId = "parts", Name = "parts", Type = ScenarioVocabulary.Classification, Classes = [.. Classes] },
            mlEnabled: false);
        return MetricsAggregator.Summarize("parts", "2026-11-05", rows, labels, new AuthorityOptions(), effective.NgLabelRule);
    }

    [Fact]
    public void Confusion_PairsTheHumanLabelWithTheDecidedClass_AndOnlyLabeledRowsCount()
    {
        var rows = new[]
        {
            Row("a", "P-100"), Row("b", "P-100"), Row("c", "P-200"), Row("d", "REJECT"), Row("e", "P-100"),
        };
        var labels = new[] { Label("a", "P-100"), Label("b", "P-200"), Label("c", "P-200"), Label("d", "REJECT") }; // e 는 라벨 없음

        var c = Summarize(rows, labels).Classification!;

        Assert.Equal(5, c.Checked);
        Assert.Equal(new Dictionary<string, int> { ["P-100"] = 3, ["P-200"] = 1, ["REJECT"] = 1 }, c.Predicted);
        Assert.Equal((4, 3), (c.Labeled, c.Agreed));
        Assert.Equal(0.75, c.AgreementRate);
        Assert.Equal(1, c.Confusion["P-100"]["P-100"]);
        Assert.Equal(1, c.Confusion["P-200"]["P-100"]);   // 사람은 P-200, 판정은 P-100 — 틀린 칸
        Assert.Equal(1, c.Confusion["REJECT"]["REJECT"]);
    }

    [Fact]
    public void NgRecall_IsTheMustCatchRecall()
    {
        var rows = new[] { Row("a", "REJECT"), Row("b", "P-100"), Row("c", "P-100") };     // 정답이 REJECT 인 둘 중 하나만 잡았다
        var labels = new[] { Label("a", "REJECT"), Label("b", "REJECT"), Label("c", "P-200") };

        var s = Summarize(rows, labels);

        Assert.Equal(2, s.LabeledNg);                 // must_catch 클래스 라벨 = NG 쪽
        Assert.Equal(1, s.VlmNgHits);
        Assert.Equal(0.5, s.VlmNgRecall);
        Assert.Equal(1, s.LabeledOk);                 // 클래스 P-200 은 꼭 잡을 값이 아니다
        Assert.Equal(0, s.VlmFalseAlarms);            // P-100 으로 낸 판정은 OK 보기라 오경보가 아니다
    }

    [Fact]
    public void WithoutMustCatchClasses_NoPromotionSignal_ButTheConfusionStillShows()
    {
        var effective = EffectiveScenario.Resolve(
            new Scenario { ScenarioId = "p", Name = "p", Type = ScenarioVocabulary.Classification, Classes = [new() { Id = "A" }, new() { Id = "B" }] },
            mlEnabled: false);
        var rows = Enumerable.Range(0, 60).Select(i => Row($"i{i}", i % 2 == 0 ? "A" : "B", Verdict.OK, ml: "A")).ToArray();
        var labels = rows.Select(r => Label(r.ImageId, "A")).ToArray();

        var s = MetricsAggregator.Summarize("p", "2026-11-05", rows, labels, new AuthorityOptions(), effective.NgLabelRule);

        Assert.Equal(0, s.LabeledNg);                 // 잡아야 할 값이 없다 → NG 쪽 라벨이 없다
        Assert.Null(s.VlmNgRecall);
        Assert.NotEqual(true, s.PromotionEligible);   // 격상 신호를 위장하지 않는다
        Assert.Equal(60, s.Classification!.Labeled);
    }

    [Fact]
    public void NonClassificationRows_ProduceNoBlock_AndNoClassField()
    {
        var row = new MetricsRow { ImageId = "x", Timestamp = "t", Verdict = Verdict.NG, MlDegraded = false, VlmOnly = true };

        Assert.Null(MetricsAggregator.Summarize("s", "d", [row], [], new AuthorityOptions()).Classification);
        Assert.DoesNotContain("\"class\"", JsonSerializer.Serialize(row));        // 다른 행의 byte 는 그대로
        Assert.DoesNotContain("\"classification\"", JsonSerializer.Serialize(
            MetricsAggregator.Summarize("s", "d", [row], [], new AuthorityOptions())));
    }
}
