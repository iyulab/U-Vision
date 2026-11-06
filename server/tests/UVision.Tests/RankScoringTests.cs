using UVision.Api.Services.Metrics;
using Xunit;

namespace UVision.Tests;

/// <summary>순위만 보는 지표 — 문턱에 기대지 않는다(격상 신호 · 측정 도구가 같은 정의를 읽는다).</summary>
public class RankScoringTests
{
    [Fact]
    public void Auroc_separated_scores_are_one_and_ties_are_half()
    {
        Assert.Equal(1.0, RankScoring.Auroc([0.1, 0.2], [0.5, 0.6]));
        Assert.Equal(0.0, RankScoring.Auroc([0.5, 0.6], [0.1, 0.2]));
        Assert.Equal(0.5, RankScoring.Auroc([0.3], [0.3]));
    }

    [Fact]
    public void MissedAtFalseAlarm_counts_defects_at_or_below_the_threshold_that_allows_the_rate()
    {
        double[] normal = [.. Enumerable.Range(1, 100).Select(i => i / 100.0)]; // 0.01 … 1.00
        // 오경보 5 % 허용 → 정상 5 개(0.96~1.00)가 문턱 위, 문턱 = 0.95.
        Assert.Equal(2, RankScoring.MissedAtFalseAlarm(normal, [0.50, 0.95, 0.96, 2.0], 0.05));
        // 오경보 0 → 문턱 = 정상 최댓값.
        Assert.Equal(2, RankScoring.MissedAtFalseAlarm(normal, [0.50, 1.00, 1.01, 2.0], 0.0));
    }

    [Fact]
    public void RecallAtFalseAlarm_IsTheShareOfDefectsCaught_AtTheAllowedFalseAlarmRate()
    {
        double[] normal = [.. Enumerable.Range(1, 100).Select(i => i / 100.0)];
        // 오경보 10 % → 문턱 0.90: 불량 0.50 · 0.90 은 통과, 0.91 · 2.0 은 잡힘 → 재현율 2/4.
        Assert.Equal(0.5, RankScoring.RecallAtFalseAlarm(normal, [0.50, 0.90, 0.91, 2.0], 0.10));
        // 정상이나 불량이 없으면 말할 수 없다.
        Assert.Null(RankScoring.RecallAtFalseAlarm([], [0.5], 0.1));
        Assert.Null(RankScoring.RecallAtFalseAlarm(normal, [], 0.1));
    }
}
