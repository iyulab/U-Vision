extern alias bench;

using bench::UVision.Benchmark;
using UVision.Api.Services.Counting;
using Xunit;

namespace UVision.Tests;

/// <summary>개수 오차 측정의 순수 부분 — 문턱별 채점 · 정답 개수 구간 · 95 분위 · 입력 검증. 합성 상자로만 돈다(실 검출기 숫자가 아니다).</summary>
public class CountErrorBenchTests
{
    private static Detection D(double confidence, string cls = "pill") => new(cls, 0, 0, 0, 0, confidence);

    private static CountErrorBench.CountCase Case(string id, int truth, params double[] confidences) =>
        new(id, truth, [.. confidences.Select(c => D(c))]);

    [Fact]
    public void Scores_exact_within_one_and_signed_error_per_threshold()
    {
        // 정답 3 · 5 · 2. 문턱 0.5: 셈 3(정확) · 6(+1) · 0(-2).
        IReadOnlyList<CountErrorBench.CountCase> cases =
        [
            Case("a", 3, 0.9, 0.9, 0.9),
            Case("b", 5, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9),
            Case("c", 2, 0.3),
        ];

        var row = CountErrorBench.Evaluate(cases, [0.5]).Rows.Single();

        Assert.Equal(3, row.N);
        Assert.Equal(1.0 / 3, row.ExactRate, precision: 6);
        Assert.Equal(2.0 / 3, row.WithinOneRate, precision: 6);
        Assert.Equal((0 + 1 - 2) / 3.0, row.MeanSignedError, precision: 6);
        Assert.Equal(2, row.MaxAbsError);
        Assert.Equal(2, row.P95AbsError); // 최근접 순위: ceil(0.95 × 3) = 3 번째
    }

    [Fact]
    public void Raising_the_threshold_moves_over_counting_to_under_counting()
    {
        IReadOnlyList<CountErrorBench.CountCase> cases = [Case("a", 2, 0.95, 0.9, 0.4, 0.35)];

        var report = CountErrorBench.Evaluate(cases, [0.3, 0.6]);

        Assert.Equal(2.0, report.Rows[0].MeanSignedError); // 문턱 0.3: 4 개를 셈(+2)
        Assert.Equal(0.0, report.Rows[1].MeanSignedError); // 문턱 0.6: 2 개 — 정확
        Assert.Equal([0.3, 0.6], report.Rows.Select(r => r.Threshold));
    }

    [Fact]
    public void Splits_by_true_count_bins_and_keeps_empty_bins_at_zero()
    {
        IReadOnlyList<CountErrorBench.CountCase> cases =
        [
            Case("zero", 0),                                         // 0 구간: 정확
            Case("few", 4, 0.9, 0.9, 0.9, 0.9),                     // 1~5: 정확
            Case("few-miss", 5, 0.9, 0.9),                           // 1~5: -3
            Case("many", 25, Enumerable.Repeat(0.9, 22).ToArray()),         // 21+: -3
        ];

        var bins = CountErrorBench.Evaluate(cases, [0.5]).Rows.Single().Bins.ToDictionary(b => b.Label);

        Assert.Equal((1, 1.0), (bins["0"].N, bins["0"].ExactRate));
        Assert.Equal((2, 0.5, 3), (bins["1~5"].N, bins["1~5"].ExactRate, bins["1~5"].MaxAbsError));
        Assert.Equal(0, bins["6~20"].N);
        Assert.Equal((1, 0.0, 3), (bins["21+"].N, bins["21+"].ExactRate, bins["21+"].MaxAbsError));
    }

    [Fact]
    public void Counts_only_the_chosen_kinds()
    {
        IReadOnlyList<CountErrorBench.CountCase> cases =
        [
            new("a", 2, [D(0.9), D(0.9), D(0.9, "head"), D(0.9, "head")]),
        ];

        Assert.Equal(1.0, CountErrorBench.Evaluate(cases, [0.5], ["pill"]).Rows.Single().ExactRate);
        Assert.Equal(0.0, CountErrorBench.Evaluate(cases, [0.5]).Rows.Single().ExactRate); // 모두 센 4 ≠ 2
    }

    [Fact]
    public void Uncertain_rate_uses_the_operating_rule_and_tolerance()
    {
        // 정답 3 · 허용 0: 확실한 2 + 띠 안의 하나(0.55) — 센 수 3 은 범위 안이지만 그 하나에 달려 있다(운영에서 사람 확인).
        IReadOnlyList<CountErrorBench.CountCase> cases = [Case("a", 3, 0.95, 0.9, 0.55)];

        Assert.Equal(1.0, CountErrorBench.Evaluate(cases, [0.5], tolerance: 0).Rows.Single().UncertainRate);
        Assert.Equal(0.0, CountErrorBench.Evaluate(cases, [0.5], tolerance: 1).Rows.Single().UncertainRate); // ±1 이면 2 도 3 도 안
    }

    [Fact]
    public void Percentile95_is_nearest_rank_and_never_interpolates()
    {
        Assert.Equal(0, CountErrorBench.Percentile95([]));
        Assert.Equal(4, CountErrorBench.Percentile95([4]));
        // 10 건: ceil(9.5) = 10 번째 = 가장 큰 값 — 작은 표본에서는 꼬리 하나가 그대로 보인다.
        Assert.Equal(7, CountErrorBench.Percentile95([0, 0, 0, 0, 0, 0, 0, 0, 0, 7]));
        // 20 건: ceil(19) = 19 번째 — 꼬리 하나(최댓값)는 95 분위 밖이고 «최대» 칸이 따로 보인다.
        Assert.Equal(0, CountErrorBench.Percentile95(Enumerable.Repeat(0, 19).Append(9)));
    }

    [Fact]
    public void Parse_reads_the_documented_shape()
    {
        var cases = CountErrorBench.Parse("""
            [{"id":"x","truth":2,"detections":[{"class":"pill","confidence":0.9},{"class":"pill","confidence":0.4}]},
             {"truth":0,"detections":[]}]
            """);

        Assert.Equal(["x", "#2"], cases.Select(c => c.Id));
        Assert.Equal(2, cases[0].Detections.Count);
        Assert.Equal(0.4, cases[0].Detections[1].Confidence);
    }

    [Theory]
    [InlineData("""[{"detections":[]}]""", "truth")]
    [InlineData("""[{"truth":-1,"detections":[]}]""", "truth")]
    [InlineData("""[{"truth":1}]""", "detections")]
    [InlineData("""[{"truth":1,"detections":[{"confidence":0.5}]}]""", "class")]
    [InlineData("""[{"truth":1,"detections":[{"class":"a","confidence":1.5}]}]""", "confidence")]
    [InlineData("""[{"truth":1,"detections":[{"class":"a"}]}]""", "confidence")]
    public void Parse_rejects_input_that_would_skew_the_score(string json, string reason)
    {
        var ex = Assert.Throws<InvalidDataException>(() => CountErrorBench.Parse(json));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void Markdown_names_the_caveats_and_carries_n_in_every_row()
    {
        IReadOnlyList<CountErrorBench.CountCase> cases = [Case("a", 3, 0.9, 0.9, 0.9)];

        var text = CountErrorBench.ToMarkdown(CountErrorBench.Evaluate(cases, [0.5]));

        Assert.Contains("# 개수 오차 측정", text);
        Assert.Contains("작은 표본의 비율은 믿을 수 없다", text);
        Assert.Contains("| 0.5 | 1 | 100.0 % |", text);
        Assert.Contains("| 0.5 | 1~5 | 1 | 100.0 % |", text);
        Assert.DoesNotContain("| 0.5 | 21+ |", text); // 사진이 없는 구간은 적지 않는다
    }
}
