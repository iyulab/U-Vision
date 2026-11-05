extern alias bench;

using bench::UVision.Benchmark;
using UVision.Api.Services.Ml;
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

    [Fact]
    public void NgScoreOf_ReadsTheNgProbability_ThenOneMinusOk_ThenTheBinaryLabel()
    {
        static MlClassification Make(string label, double confidence, params (string Cls, double P)[] scores) => new()
        {
            Label = label, Confidence = confidence, Scores = scores.ToDictionary(s => s.Cls, s => s.P),
        };

        // 대소문자를 무시하고 NG 쪽 확률을 그대로 — 예측 라벨이 OK 여도 NG 점수는 NG 확률이다.
        Assert.Equal(0.3, ClassifyBench.NgScoreOf(Make("ok", 0.7, ("ng", 0.3), ("ok", 0.7))));
        // NG 가 없고 OK 만 있으면 1 − OK.
        Assert.Equal(0.25, ClassifyBench.NgScoreOf(Make("OK", 0.75, ("OK", 0.75))));
        // 확률 맵이 비면 예측 라벨의 확률로 이진 환산.
        Assert.Equal(0.9, ClassifyBench.NgScoreOf(Make("NG", 0.9)));
        Assert.Equal(0.2, ClassifyBench.NgScoreOf(Make("ok", 0.8))!.Value, precision: 10);
        // 이진이 아닌 라벨은 읽을 수 없다 — 그 사진은 문턱 무관 지표에서 빠진다.
        Assert.Null(ClassifyBench.NgScoreOf(Make("scratch", 0.6)));
    }

    [Fact]
    public void Report_ReadsAurocAndRecallAtFixedFalseAlarms_FromTheScores_TiesCountAsPassed()
    {
        List<Sample> test =
        [
            .. Enumerable.Range(0, 4).Select(i => new Sample($"ng/{i}", "test", "NG", $"n{i}")),
            .. Enumerable.Range(0, 10).Select(i => new Sample($"ok/{i}", "test", "OK", $"o{i}")),
        ];
        var tally = new Dictionary<(string Truth, string Predicted), int> { [("NG", "NG")] = 3, [("NG", "OK")] = 1, [("OK", "OK")] = 10 };
        // 정상 점수 0.0 ~ 0.9, 불량 점수 0.95 · 0.9(정상 최댓값과 동점) · 0.85 · 0.5.
        List<(string Truth, double NgScore)> scores =
        [
            .. Enumerable.Range(0, 10).Select(i => ("OK", i / 10.0)),
            ("NG", 0.95), ("NG", 0.9), ("NG", 0.85), ("NG", 0.5),
        ];

        var report = ClassifyBench.Render("m", test, tally, [], failed: 0, scores);

        // AUROC: 불량마다 이긴 정상 수 10 + 9.5(동점 절반) + 9 + 5.5 = 34 → 34/40.
        Assert.Contains("AUROC 0.850", report);
        Assert.Contains("NG 점수 14/14장", report);
        // 허용 1 % · 5 % 는 정상 10장에서 0장 → 문턱 0.9, 0.9 와 동점인 불량은 통과 → 0.95 하나만 잡힘.
        Assert.Contains("| 1 % | 0/10 | 0.250 (1/4) |", report);
        Assert.Contains("| 5 % | 0/10 | 0.250 (1/4) |", report);
        // 허용 10 % = 1장 → 문턱 0.8 → 0.95 · 0.9 · 0.85 가 잡힘.
        Assert.Contains("| 10 % | 1/10 | 0.750 (3/4) |", report);
    }

    [Fact]
    public void Report_WithoutScores_KeepsTheOldShape()
    {
        List<Sample> test = [new("ng/0", "test", "NG", "n0"), new("ok/0", "test", "OK", "o0")];
        var tally = new Dictionary<(string Truth, string Predicted), int> { [("NG", "NG")] = 1, [("OK", "OK")] = 1 };

        Assert.DoesNotContain("문턱 무관", ClassifyBench.Render("m", test, tally, [], failed: 0));
        // 한쪽 클래스만 점수가 있으면 계산하지 않고 이유를 말한다.
        Assert.Contains("정상 · 불량이 모두 있어야", ClassifyBench.Render("m", test, tally, [], failed: 0, [("NG", 0.9)]));
    }

    [Fact]
    public void Export_CapsEachClassEvenly_AndCopiesOnlyTrain()
    {
        var root = Directory.CreateTempSubdirectory("uvision-export-").FullName;
        try
        {
            List<Sample> samples = [];
            foreach (var cls in new[] { "NG", "OK" })
                for (var i = 0; i < 10; i++)
                {
                    var rel = $"src/{cls}{i}.jpg";
                    Directory.CreateDirectory(Path.Combine(root, "src"));
                    File.WriteAllBytes(Path.Combine(root, rel), [1, (byte)i]);
                    samples.Add(new Sample(rel, i < 8 ? "train" : "test", cls, $"{cls}{i:D14}ffff"));
                }
            var export = Path.Combine(root, "out");

            Assert.Equal(0, ClassifyBench.Export(root, samples, export, perClass: 4));

            Assert.Equal(4, Directory.GetFiles(Path.Combine(export, "NG")).Length);   // train 8 중 고르게 4
            Assert.Equal(4, Directory.GetFiles(Path.Combine(export, "OK")).Length);
            Assert.Equal(0, ClassifyBench.Export(root, samples, Path.Combine(root, "all"), perClass: int.MaxValue));
            Assert.Equal(8, Directory.GetFiles(Path.Combine(root, "all", "NG")).Length);  // test 분할은 안 나간다
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReportFile_CreatesTheFolder_AndWritesKoreanTextWithoutBom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uvision-report-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sub", "report.md");
        try
        {
            await ReportFile.WriteAsync(path, "# 리포트\n불량 재현율 1.000");

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("# 리포트\n불량 재현율 1.000", System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
