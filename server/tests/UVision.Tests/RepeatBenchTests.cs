extern alias bench;

using bench::UVision.Benchmark;
using Xunit;

namespace UVision.Tests;

/// <summary>반복 일관성 — 같은 표본을 두 번 판정한 리포트에서 잡음과 체계적 오류가 갈린다.</summary>
public class RepeatBenchTests
{
    private static string Report(string dir, string name, params (string Image, string Truth, string Verdict)[] rows)
    {
        var lines = new List<string>
        {
            "# VLM 벤치마크 리포트", "", "## 건별 결과",
            "| 이미지 | 정답 | 판정 | confidence | latency(ms) | 근거 |", "|---|---|---|---|---|---|",
        };
        lines.AddRange(rows.Select(r => $"| {r.Image} | {r.Truth} | {r.Verdict} | 0.90 | 1000 | |"));
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Splits_NoiseFromSystematicError_AndSkipsUnansweredRepeats()
    {
        var dir = Directory.CreateTempSubdirectory("uvision-repeat-").FullName;
        try
        {
            var a = Report(dir, "a.md",
                ("i1", "OK", "OK"), ("i2", "OK", "NG"), ("i3", "NG", "NG"), ("i4", "NG", "OK"), ("i5", "NG", "**실패**"));
            var b = Report(dir, "b.md",
                ("i1", "OK", "OK"), ("i2", "OK", "OK"), ("i3", "NG", "NG"), ("i4", "NG", "OK"), ("i5", "NG", "NG"));
            var outPath = Path.Combine(dir, "out.md");

            var exit = RepeatBench.Run(new Dictionary<string, string> { ["a"] = a, ["b"] = b, ["out"] = outPath });

            Assert.Equal(0, exit);
            var text = File.ReadAllText(outPath);
            Assert.Contains("둘 다 답한 반복 **4**", text);                      // i5 는 한쪽이 판정 불가라 빠진다
            Assert.Contains("| 일관성 | 75.0 % |", text);                       // i2 만 갈린다
            Assert.Contains("| 잡음(NoiseRate) | 25.0 % |", text);
            Assert.Contains("| 체계적 오류(SystematicErrorRate) | 25.0 % |", text);   // i4: 두 번 다 NG 를 OK 로
            Assert.Contains("| 일치했을 때 정확도 | 66.7 % |", text);           // 일치한 i1 · i3 · i4 중 둘 다 맞은 것 2
            Assert.Contains("| 불량 판정이 뒤집힌 수 | 1 |", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoCommonImages_IsAnError()
    {
        var dir = Directory.CreateTempSubdirectory("uvision-repeat-").FullName;
        try
        {
            var a = Report(dir, "a.md", ("x", "OK", "OK"));
            var b = Report(dir, "b.md", ("y", "OK", "OK"));
            Assert.Equal(1, RepeatBench.Run(new Dictionary<string, string> { ["a"] = a, ["b"] = b }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
