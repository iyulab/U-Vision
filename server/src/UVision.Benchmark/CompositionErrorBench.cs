using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UVision.Api.Services.Counting;

namespace UVision.Benchmark;

/// <summary>
/// 구성 확인(종류별 기대)의 오차 측정 — «기대한 구성을 맞게 가려내는가» 를 사진별 항목 목록과 판정으로 본다.
/// 입력은 사진마다 종류별 기대 · 종류별 정답 개수 · 검출기가 낸 상자(종류 · 신뢰도)이고, 신뢰도 문턱을 달리하며 채점한다:
/// 항목 목록 일치율 · 누락 recall / precision(참 누락을 <c>missing</c> 으로 잡았나) · 과잉 recall / precision ·
/// 사진 판정(OK/NG) 일치 — 특히 <b>NG 를 OK 로 통과시킨 수</b>(놓침)와 정상을 NG 로 막은 수(오경보).
/// 정답 항목은 정답 개수를 신뢰도 1 의 상자로 바꿔 <b>판정과 같은 순수 함수</b>(<see cref="CompositionEvaluator"/>)에 넣어 만든다 —
/// 측정 변환 = 운영 변환. 검출기를 부르지 않는다: 상자는 입력 파일에 있다.
/// <para>
/// ⚠️ 이 도구가 만드는 숫자는 입력한 상자의 것이지 검출기 일반의 것이 아니다(실 모델 · 실 사진으로 돌리기 전까지 UNVERIFIED).
/// 표본이 작으면 비율은 믿을 수 없다 — 칸마다 분모를 함께 적는다.
/// </para>
/// </summary>
internal static class CompositionErrorBench
{
    internal static readonly double[] DefaultThresholds = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9];

    /// <summary>입력 한 사진 — 종류별 기대 · 종류별 정답 개수 · 검출기가 낸 상자.</summary>
    internal sealed record CompositionCase(
        string Id, IReadOnlyDictionary<string, int> Expected, IReadOnlyDictionary<string, int> Truth, IReadOnlyList<Detection> Detections);

    /// <summary>한 칸의 «맞힌 수 / 분모» — 분모가 0 이면 비율은 정의되지 않는다(<see cref="Rate"/> 가 null).</summary>
    internal readonly record struct Ratio(int Hit, int Of)
    {
        public double? Rate => Of == 0 ? null : (double)Hit / Of;
    }

    internal sealed record ThresholdRow(
        double Threshold, int N, Ratio ListMatch, Ratio MissingRecall, Ratio MissingPrecision, Ratio ExtraRecall, Ratio ExtraPrecision,
        Ratio VerdictMatch, int Escapes, int FalseAlarms, Ratio Uncertain);

    internal sealed record Report(int Cases, string ExtraPolicy, IReadOnlyList<ThresholdRow> Rows);

    internal static Report Evaluate(
        IReadOnlyList<CompositionCase> cases, IReadOnlyList<double> thresholds, string extraPolicy = ExtraPolicies.Ng,
        IReadOnlyList<string>? classes = null)
    {
        // 정답: 정답 개수를 확실한 상자(신뢰도 1)로 바꿔 같은 함수로 항목 · 판정을 만든다.
        var truths = cases.Select(c => CompositionEvaluator.Evaluate(
            [.. c.Truth.SelectMany(kv => Enumerable.Range(0, kv.Value).Select(_ => new Detection(kv.Key, 0, 0, 0, 0, 1.0)))],
            0.5, null, c.Expected, extraPolicy)).ToList();

        var rows = new List<ThresholdRow>();
        foreach (var threshold in thresholds.Order())
        {
            var scored = cases.Select((c, i) =>
            {
                var predicted = CompositionEvaluator.Evaluate(c.Detections, threshold, classes, c.Expected, extraPolicy);
                return (Predicted: predicted, Truth: truths[i]);
            }).ToList();

            int listHit = 0, missHit = 0, missTrue = 0, missPred = 0, extraHit = 0, extraTrue = 0, extraPred = 0;
            int verdictHit = 0, escapes = 0, falseAlarms = 0, uncertain = 0;
            foreach (var (predicted, truth) in scored)
            {
                var p = StatusByKind(predicted);
                var t = StatusByKind(truth);
                if (p.Count == t.Count && p.All(kv => t.TryGetValue(kv.Key, out var s) && s == kv.Value)) listHit++;
                foreach (var kind in p.Keys.Union(t.Keys))
                {
                    var ps = p.GetValueOrDefault(kind, ItemStatus.Ok);
                    var ts = t.GetValueOrDefault(kind, ItemStatus.Ok);
                    if (ts == ItemStatus.Missing) { missTrue++; if (ps == ts) missHit++; }
                    if (ps == ItemStatus.Missing) missPred++;
                    if (ts == ItemStatus.Extra) { extraTrue++; if (ps == ts) extraHit++; }
                    if (ps == ItemStatus.Extra) extraPred++;
                }

                // 운영 판정과 같은 규칙: 구성이 맞고 흔들리지 않을 때만 OK.
                var predictedOk = predicted.WithinRange == true && !predicted.Uncertain;
                var truthOk = truth.WithinRange == true;
                if (predictedOk == truthOk) verdictHit++;
                if (predictedOk && !truthOk) escapes++;
                if (!predictedOk && truthOk) falseAlarms++;
                if (predicted.Uncertain) uncertain++;
            }

            var n = scored.Count;
            rows.Add(new ThresholdRow(
                threshold, n, new(listHit, n),
                new(missHit, missTrue), new(missHit, missPred), new(extraHit, extraTrue), new(extraHit, extraPred),
                new(verdictHit, n), escapes, falseAlarms, new(uncertain, n)));
        }
        return new Report(cases.Count, extraPolicy, rows);
    }

    /// <summary>종류 → 상태. 항목에 오르지 않은 종류(과잉을 무시한 기대 밖 종류)는 없는 것이다.</summary>
    private static Dictionary<string, string> StatusByKind(CountResult result) =>
        result.Items!.ToDictionary(i => i.Class, i => i.Status, StringComparer.Ordinal);

    private static string Pct(Ratio r) => r.Rate is { } v ? string.Create(CultureInfo.InvariantCulture, $"{v:P1} ({r.Hit}/{r.Of})") : $"— (0/{r.Of})";

    internal static string ToMarkdown(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 구성 확인 오차 측정");
        sb.AppendLine();
        sb.AppendLine($"사진 {report.Cases}장 · 과잉 처리 `{report.ExtraPolicy}`. 정답 항목은 정답 개수를 판정과 같은 함수에 넣어 만들었다. 칸은 «맞힌 수 / 분모».");
        sb.AppendLine("입력한 상자의 숫자이지 검출기 일반의 숫자가 아니다 — 분모가 작은 칸은 믿을 수 없다.");
        sb.AppendLine();
        sb.AppendLine("## 신뢰도 문턱별");
        sb.AppendLine();
        sb.AppendLine("| 문턱 | n | 항목 목록 일치 | 누락 recall | 누락 precision | 과잉 recall | 과잉 precision | 판정 일치 | **놓침(NG→OK)** | 오경보(OK→NG) | 흔들림 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in report.Rows)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Threshold:0.##} | {r.N} | {Pct(r.ListMatch)} | {Pct(r.MissingRecall)} | {Pct(r.MissingPrecision)} | {Pct(r.ExtraRecall)} | {Pct(r.ExtraPrecision)} | {Pct(r.VerdictMatch)} | {r.Escapes} | {r.FalseAlarms} | {Pct(r.Uncertain)} |"));
        sb.AppendLine();
        sb.AppendLine("놓침 = 정답이 NG(구성이 틀림)인데 OK 로 통과한 사진 수 — 구성 확인에서 가장 비싼 오류다. 누락 recall 은 참 누락(사진, 종류)쌍 중 `missing` 으로 잡은 비율.");
        return sb.ToString();
    }

    // ── 입력 파일 ─────────────────────────────────────────────────────

    private sealed class CaseDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("expected")] public Dictionary<string, int>? Expected { get; set; }
        [JsonPropertyName("truth")] public Dictionary<string, int>? Truth { get; set; }
        [JsonPropertyName("detections")] public List<DetectionDto>? Detections { get; set; }
    }

    private sealed class DetectionDto
    {
        [JsonPropertyName("class")] public string? Class { get; set; }
        [JsonPropertyName("confidence")] public double? Confidence { get; set; }
    }

    /// <summary>
    /// 입력 JSON — <c>[{"id": "...", "expected": {"helmet": 2}, "truth": {"helmet": 1}, "detections": [{"class": "helmet", "confidence": 0.9}]}]</c>.
    /// 기대 · 정답이 없거나 음수이거나 신뢰도가 [0,1] 이 아니면 조용히 거르지 않고 거부한다(채점이 틀어진다).
    /// </summary>
    internal static IReadOnlyList<CompositionCase> Parse(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<CaseDto>>(json) ?? throw new InvalidDataException("입력이 비었다");
        var cases = new List<CompositionCase>();
        for (var i = 0; i < dtos.Count; i++)
        {
            var d = dtos[i];
            var id = d.Id ?? $"#{i + 1}";
            if (d.Expected is not { Count: > 0 } expected || expected.Values.Any(n => n < 0))
                throw new InvalidDataException($"{id}: expected(종류별 기대 개수)는 비어 있지 않고 0 이상이어야 한다");
            if (d.Truth is not { } truth || truth.Values.Any(n => n < 0))
                throw new InvalidDataException($"{id}: truth(종류별 정답 개수)는 0 이상이어야 한다(없는 종류는 생략)");
            if (d.Detections is null) throw new InvalidDataException($"{id}: detections 가 없다(상자가 없으면 빈 목록)");
            var boxes = new List<Detection>();
            foreach (var det in d.Detections)
            {
                if (string.IsNullOrEmpty(det.Class)) throw new InvalidDataException($"{id}: 상자에 class 가 없다");
                if (det.Confidence is not { } c || !(c >= 0 && c <= 1)) throw new InvalidDataException($"{id}: 상자의 confidence 는 0~1 이어야 한다");
                boxes.Add(new Detection(det.Class, 0, 0, 0, 0, c));
            }
            cases.Add(new CompositionCase(id, expected, truth, boxes));
        }
        return cases;
    }

    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("input", out var path) || !File.Exists(path))
        {
            Console.Error.WriteLine("composition-error: --input <사진별 종류별 기대 · 정답 · 상자 JSON> 필수 (선택: --thresholds 0.3,0.5,0.7 · --extra-policy ng|warn|ignore · --classes a,b · --out report.md)");
            return 1;
        }
        IReadOnlyList<CompositionCase> cases;
        try { cases = Parse(await File.ReadAllTextAsync(path)); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            Console.Error.WriteLine($"composition-error: 입력 오류 — {ex.Message}");
            return 1;
        }
        if (cases.Count == 0) { Console.Error.WriteLine("composition-error: 사진이 없다"); return 1; }

        var thresholds = opts.TryGetValue("thresholds", out var t)
            ? t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray()
            : DefaultThresholds;
        if (thresholds.Any(x => !(x > 0 && x <= 1))) { Console.Error.WriteLine("composition-error: --thresholds 는 0 초과 1 이하"); return 1; }
        var policy = opts.GetValueOrDefault("extra-policy") ?? ExtraPolicies.Ng;
        if (!ExtraPolicies.All.Contains(policy)) { Console.Error.WriteLine($"composition-error: --extra-policy 는 {string.Join(", ", ExtraPolicies.All)}"); return 1; }
        var classes = opts.GetValueOrDefault("classes")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var markdown = ToMarkdown(Evaluate(cases, thresholds, policy, classes));
        if (opts.TryGetValue("out", out var outPath)) await ReportFile.WriteAsync(outPath, markdown);
        else Console.Out.Write(markdown);
        return 0;
    }
}
