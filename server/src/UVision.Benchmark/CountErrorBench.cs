using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UVision.Api.Services.Counting;

namespace UVision.Benchmark;

/// <summary>
/// 계수 검출기의 개수 오차 측정 — «이 검출기를 믿어도 되는가» 를 평균이 아니라 <b>꼬리</b>로 본다(계수 설계서 §5).
/// 입력은 사진마다 정답 개수와 검출기가 낸 상자(종류 · 신뢰도)이고, 신뢰도 문턱을 달리하며 사진별 <c>|세어낸 개수 − 정답|</c> 의
/// 분포를 낸다: 정확히 맞는 비율 · ±1 이내 비율 · 부호 있는 평균(과대 · 과소 계수) · 오차의 95 분위와 최댓값, 그리고 정답 개수
/// 구간(0 · 1~5 · 6~20 · 21+)별로 따로 — 개수가 클수록 어려워지므로 섞지 않는다. 판정과 같은 순수 함수(<see cref="CountEvaluator"/>)로
/// 센다 — 측정 변환 = 운영 변환. 검출기를 부르지 않는다: 상자는 입력 파일에 있다(검출 서빙 왕복과 채점을 분리).
/// <para>
/// ⚠️ 표본이 작으면 비율은 믿을 수 없다 — 모든 칸에 사진 수(<c>n</c>)를 함께 적는다. 이 도구가 만드는 숫자는 입력한 상자의 것이지
/// 검출기 일반의 것이 아니다(실 모델 · 실 사진으로 돌리기 전까지 UNVERIFIED).
/// </para>
/// </summary>
internal static class CountErrorBench
{
    /// <summary>정답 개수 구간 — 개수가 클수록 어려워지므로 따로 본다.</summary>
    internal static readonly (string Label, int Min, int Max)[] Bins =
    [
        ("0", 0, 0), ("1~5", 1, 5), ("6~20", 6, 20), ("21+", 21, int.MaxValue),
    ];

    internal static readonly double[] DefaultThresholds = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9];

    /// <summary>입력 한 사진 — 정답 개수와 검출기가 낸 상자.</summary>
    internal sealed record CountCase(string Id, int Truth, IReadOnlyList<Detection> Detections);

    internal sealed record BinStat(string Label, int N, double ExactRate, double WithinOneRate, int MaxAbsError);

    internal sealed record ThresholdRow(
        double Threshold, int N, double ExactRate, double WithinOneRate, double MeanSignedError,
        int P95AbsError, int MaxAbsError, double UncertainRate, IReadOnlyList<BinStat> Bins);

    internal sealed record Report(int Cases, int Tolerance, IReadOnlyList<ThresholdRow> Rows);

    /// <summary>
    /// 문턱마다 채점한다. <paramref name="tolerance"/> 는 «정확히 맞음» 대신 쓰는 허용 오차가 아니라 판정(<c>WithinRange</c>)의 허용
    /// 오차다 — <c>UncertainRate</c>(운영에서 사람 확인으로 가는 비율)를 같은 규칙으로 재려고 받는다.
    /// </summary>
    internal static Report Evaluate(
        IReadOnlyList<CountCase> cases, IReadOnlyList<double> thresholds, IReadOnlyList<string>? classes = null, int tolerance = 0)
    {
        var rows = new List<ThresholdRow>();
        foreach (var threshold in thresholds.Order())
        {
            var scored = cases.Select(c =>
            {
                var counted = CountEvaluator.Count(c.Detections, threshold, classes, c.Truth, tolerance);
                return (Case: c, Counted: counted.Total, Uncertain: counted.Uncertain);
            }).ToList();

            rows.Add(new ThresholdRow(
                threshold, scored.Count,
                Rate(scored, s => s.Counted == s.Case.Truth),
                Rate(scored, s => Math.Abs(s.Counted - s.Case.Truth) <= 1),
                scored.Count == 0 ? 0 : scored.Average(s => (double)(s.Counted - s.Case.Truth)),
                Percentile95(scored.Select(s => Math.Abs(s.Counted - s.Case.Truth))),
                scored.Count == 0 ? 0 : scored.Max(s => Math.Abs(s.Counted - s.Case.Truth)),
                Rate(scored, s => s.Uncertain),
                Bins.Select(bin =>
                {
                    var inBin = scored.Where(s => s.Case.Truth >= bin.Min && s.Case.Truth <= bin.Max).ToList();
                    return new BinStat(
                        bin.Label, inBin.Count,
                        Rate(inBin, s => s.Counted == s.Case.Truth),
                        Rate(inBin, s => Math.Abs(s.Counted - s.Case.Truth) <= 1),
                        inBin.Count == 0 ? 0 : inBin.Max(s => Math.Abs(s.Counted - s.Case.Truth)));
                }).ToList()));
        }
        return new Report(cases.Count, tolerance, rows);
    }

    private static double Rate<T>(IReadOnlyCollection<T> items, Func<T, bool> predicate) =>
        items.Count == 0 ? 0 : (double)items.Count(predicate) / items.Count;

    /// <summary>가장 가까운 순위 방식의 95 분위 — 작은 표본에서 보간으로 없는 값을 지어내지 않는다.</summary>
    internal static int Percentile95(IEnumerable<int> absErrors)
    {
        var sorted = absErrors.Order().ToList();
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(0.95 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>리포트 마크다운 — 문턱별 표와 정답 개수 구간별 표.</summary>
    internal static string ToMarkdown(Report report)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# 개수 오차 측정");
        sb.AppendLine();
        sb.AppendLine($"사진 {report.Cases}장 · 허용 오차 {report.Tolerance}(흔들림 판정에만 쓴다). 오차 = |세어낸 개수 − 정답|, 비율은 사진 수 대비.");
        sb.AppendLine("작은 표본의 비율은 믿을 수 없다 — 칸마다 n 을 함께 본다. 입력한 상자의 숫자이지 검출기 일반의 숫자가 아니다.");
        sb.AppendLine();
        sb.AppendLine("## 신뢰도 문턱별");
        sb.AppendLine();
        sb.AppendLine("| 문턱 | n | 정확히 맞음 | ±1 이내 | 평균 부호 오차 | 95 분위 | 최대 | 흔들림(사람 확인) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in report.Rows)
            sb.AppendLine(string.Create(inv,
                $"| {r.Threshold:0.##} | {r.N} | {r.ExactRate:P1} | {r.WithinOneRate:P1} | {r.MeanSignedError:+0.00;-0.00;0.00} | {r.P95AbsError} | {r.MaxAbsError} | {r.UncertainRate:P1} |"));
        sb.AppendLine();
        sb.AppendLine("부호 오차 > 0 은 과대 계수(없는 것을 셈), < 0 은 과소 계수(있는 것을 놓침)다.");
        sb.AppendLine();
        sb.AppendLine("## 정답 개수 구간별");
        sb.AppendLine();
        sb.AppendLine("| 문턱 | 구간 | n | 정확히 맞음 | ±1 이내 | 최대 오차 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in report.Rows)
            foreach (var b in r.Bins.Where(b => b.N > 0))
                sb.AppendLine(string.Create(inv, $"| {r.Threshold:0.##} | {b.Label} | {b.N} | {b.ExactRate:P1} | {b.WithinOneRate:P1} | {b.MaxAbsError} |"));
        return sb.ToString();
    }

    // ── 입력 파일 ─────────────────────────────────────────────────────

    private sealed class CaseDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("truth")] public int? Truth { get; set; }
        [JsonPropertyName("detections")] public List<DetectionDto>? Detections { get; set; }
    }

    private sealed class DetectionDto
    {
        [JsonPropertyName("class")] public string? Class { get; set; }
        [JsonPropertyName("confidence")] public double? Confidence { get; set; }
    }

    /// <summary>
    /// 입력 JSON — <c>[{"id": "...", "truth": 5, "detections": [{"class": "helmet", "confidence": 0.93}, ...]}, ...]</c>.
    /// 정답이 없거나 음수이거나 신뢰도가 [0,1] 이 아니면 조용히 거르지 않고 거부한다(채점이 틀어진다).
    /// </summary>
    internal static IReadOnlyList<CountCase> Parse(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<CaseDto>>(json) ?? throw new InvalidDataException("입력이 비었다");
        var cases = new List<CountCase>();
        for (var i = 0; i < dtos.Count; i++)
        {
            var d = dtos[i];
            var id = d.Id ?? $"#{i + 1}";
            if (d.Truth is not { } truth || truth < 0) throw new InvalidDataException($"{id}: truth(정답 개수)는 0 이상의 정수여야 한다");
            if (d.Detections is null) throw new InvalidDataException($"{id}: detections 가 없다(상자가 없으면 빈 목록)");
            var boxes = new List<Detection>();
            foreach (var det in d.Detections)
            {
                if (string.IsNullOrEmpty(det.Class)) throw new InvalidDataException($"{id}: 상자에 class 가 없다");
                if (det.Confidence is not { } c || !(c >= 0 && c <= 1)) throw new InvalidDataException($"{id}: 상자의 confidence 는 0~1 이어야 한다");
                // 좌표는 개수에 쓰이지 않는다 — 센 결과만 잰다.
                boxes.Add(new Detection(det.Class, 0, 0, 0, 0, c));
            }
            cases.Add(new CountCase(id, truth, boxes));
        }
        return cases;
    }

    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("input", out var path) || !File.Exists(path))
        {
            Console.Error.WriteLine("count-error: --input <사진별 정답 개수 + 상자 JSON> 필수 (선택: --thresholds 0.3,0.5,0.7 · --classes helmet,head · --tolerance 1 · --out report.md)");
            return 1;
        }
        IReadOnlyList<CountCase> cases;
        try { cases = Parse(await File.ReadAllTextAsync(path)); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            Console.Error.WriteLine($"count-error: 입력 오류 — {ex.Message}");
            return 1;
        }
        if (cases.Count == 0) { Console.Error.WriteLine("count-error: 사진이 없다"); return 1; }

        var thresholds = opts.TryGetValue("thresholds", out var t)
            ? t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray()
            : DefaultThresholds;
        if (thresholds.Any(x => !(x > 0 && x <= 1))) { Console.Error.WriteLine("count-error: --thresholds 는 0 초과 1 이하"); return 1; }
        var classes = opts.GetValueOrDefault("classes")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tolerance = int.TryParse(opts.GetValueOrDefault("tolerance"), out var tol) && tol >= 0 ? tol : 0;

        var markdown = ToMarkdown(Evaluate(cases, thresholds, classes, tolerance));
        if (opts.TryGetValue("out", out var outPath)) await ReportFile.WriteAsync(outPath, markdown);
        else Console.Out.Write(markdown);
        return 0;
    }
}
