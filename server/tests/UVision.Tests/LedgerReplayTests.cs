extern alias bench;

using bench::UVision.Benchmark;
using Xunit;

namespace UVision.Tests;

/// <summary>장부 줄 재생 — 기억 크기별로 두 점수를 같은 기준(AUROC · 같은 오경보율의 놓친 불량)으로 비교한다.</summary>
public class LedgerReplayTests
{
    private static LedgerReplay.ScoredCase Case(double global, double patch, int bank, bool defect) => new(global, patch, bank, defect);

    [Fact]
    public void Render_SplitsByBankSize_AndComparesTheTwoScoresOnTheSameCases()
    {
        // 기억 60장: 패치는 불량을 완전히 가르고(0.9 > 0.1) 전역은 섞인다. 기억 5장: 둘 다 같은 순위.
        List<LedgerReplay.ScoredCase> cases =
        [
            .. Enumerable.Range(0, 40).Select(i => Case(0.01 + i * 0.0001, 0.10 + i * 0.001, 60, defect: false)),
            .. Enumerable.Range(0, 40).Select(i => Case(i % 2 == 0 ? 0.005 : 0.02, 0.90 + i * 0.001, 60, defect: true)),
            Case(0.1, 0.1, 5, false), Case(0.9, 0.9, 5, true),
        ];

        var report = LedgerReplay.Render("demo", 100, 10, 8, cases);

        Assert.Contains("재생 82", report);
        Assert.Contains("| 50~199장 | 40 | 40 | 0.", report);          // 전역 AUROC 는 1 보다 작고
        Assert.Contains("→ 1.000 |", report);                         // 패치는 완전 분리
        Assert.Contains("| 1~9장 | 1 | 1 | 1.000 → 1.000 |", report);
        Assert.Contains("표본 적음", report);                          // 5장 구간은 표본이 모자란다
        Assert.Contains("| 전체(크기 섞임 — 참고만) |", report);
    }

    [Fact]
    public void Render_MarksAGroupWithOneClassMissing()
    {
        var report = LedgerReplay.Render("demo", 3, 0, 0, [Case(0.1, 0.1, 20, false), Case(0.1, 0.2, 20, false)]);
        Assert.Contains("한쪽 클래스가 없음", report);
    }
}
