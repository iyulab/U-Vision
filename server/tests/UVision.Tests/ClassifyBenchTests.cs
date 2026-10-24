extern alias bench;

using bench::UVision.Benchmark;
using Xunit;

namespace UVision.Tests;

/// <summary>ML 분류 정확도 리포트 — 혼동표 · 불량 재현율(불량을 OK 로 넘긴 수)이 셈과 맞는다.</summary>
public class ClassifyBenchTests
{
    [Fact]
    public void Report_CountsTheConfusion_AndTheDefectsPassedAsOk()
    {
        List<Sample> test =
        [
            .. Enumerable.Range(0, 4).Select(i => new Sample($"ng/{i}", "test", "NG", $"n{i}")),
            .. Enumerable.Range(0, 6).Select(i => new Sample($"ok/{i}", "test", "OK", $"o{i}")),
        ];
        var tally = new Dictionary<(string Truth, string Predicted), int>
        {
            [("NG", "NG")] = 3, [("NG", "OK")] = 1, [("OK", "OK")] = 5, [("OK", "NG")] = 1,
        };

        var report = ClassifyBench.Render("m", test, tally, [10, 20, 30], failed: 0);

        Assert.Contains("| NG | 3 | 1 |", report);
        Assert.Contains("| OK | 1 | 5 |", report);
        Assert.Contains("정확도 0.800 (8/10)", report);
        Assert.Contains("불량 재현율 0.750 (3/4) · 불량을 OK 로 1장", report);
        Assert.Contains("정상 오경보 1/6", report);
    }
}
