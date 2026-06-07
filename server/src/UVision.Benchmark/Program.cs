using System.Diagnostics;
using System.Globalization;
using System.Text;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;

// VLM 벤치마크 하네스 — M0.1(VLM 왕복 실측)의 실행 경로.
// Api 의 실 VlmProviderFactory/Models 를 재사용한다 → 실측이 production provider 경로를 검증한다.
// (원본: server/benchmark/run_benchmark.py 동등 이식)
//
// 사용법:
//   # mock 시연
//   dotnet run --project src/UVision.Benchmark -- --images-dir ./benchmark/sample-images
//   # 실측 (키 확보 후) — ground truth 는 images-dir/{ok,ng}/ 하위폴더로 라벨링
//   VLM_API_KEY=sk-... dotnet run --project src/UVision.Benchmark -- \
//       --images-dir ./samples --provider openai --model gpt-4o \
//       --out ../claudedocs/spikes/vlm-benchmark.md

const string DefaultCriteria =
    "제품 표면에 긁힘, 이물질, 균열, 솔더 브릿지 등 외관 결함이 없어야 한다. " +
    "결함이 보이면 NG, 깨끗하면 OK.";

// USD per 1M tokens — 공개가 기준(참고용). 실 토큰은 provider usage 로 채운다.
var pricing = new (string Model, double In, double Out)[]
{
    ("gpt-4o", 2.50, 10.00),
    ("gemini-2.5-flash", 0.30, 2.50),
    ("gemini-1.5-pro", 1.25, 5.00),
};

var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

// 개발 편의: .env 로딩(VLM_API_KEY 등).
DotNetEnv.Env.TraversePath().Load();

var opts = ParseArgs(args);
if (!opts.TryGetValue("images-dir", out var imagesDir))
{
    Console.Error.WriteLine("이미지를 찾지 못함: --images-dir 필수");
    return 1;
}
var providerName = opts.GetValueOrDefault("provider", "mock");
var model = opts.GetValueOrDefault("model", "gpt-4o");
var criteria = opts.GetValueOrDefault("criteria", DefaultCriteria);
var outPath = opts.GetValueOrDefault("out");
// 셀프호스트 provider(gpustack) base URL — --endpoint 우선, 없으면 VLM_ENDPOINT 환경변수.
var endpoint = opts.GetValueOrDefault("endpoint")
    ?? Environment.GetEnvironmentVariable("VLM_ENDPOINT") ?? "";
// few-shot 기준 이미지: --refs-dir/{ok,ng} 에서 로드(라벨당 최대 --refs-cap, 기본 3). 누수 방지 위해
// 평가셋(--images-dir)과 다른 디렉토리(예: 학습셋)를 지정할 것. 미지정 시 zero-shot.
var refsDir = opts.GetValueOrDefault("refs-dir");
var refsCap = int.TryParse(opts.GetValueOrDefault("refs-cap"), out var rc) ? rc : 3;
var references = refsDir is null ? [] : LoadReferences(refsDir, refsCap);

var images = DiscoverImages(imagesDir);
if (images.Count == 0)
{
    Console.Error.WriteLine($"이미지를 찾지 못함: {imagesDir}");
    return 1;
}

var vlmOptions = new VlmOptions
{
    Provider = providerName,
    Model = model,
    ApiKey = Environment.GetEnvironmentVariable("VLM_API_KEY") ?? "",
    Endpoint = endpoint,
};
var provider = VlmProviderFactory.Create(vlmOptions);
var scenario = new ScenarioContext
{
    ScenarioId = "benchmark",
    Criteria = criteria,
    References = references,
};
Console.WriteLine($"few-shot 기준 이미지: {references.Count}장"
    + (references.Count > 0 ? $" (OK {references.Count(r => r.Label == ReferenceLabel.Ok)}/NG {references.Count(r => r.Label == ReferenceLabel.Ng)})" : " (zero-shot)"));

var rows = new List<Row>();
foreach (var (path, truth) in images)
{
    var data = await File.ReadAllBytesAsync(path);
    var sw = Stopwatch.StartNew();
    var result = await provider.InspectAsync(data, scenario);
    sw.Stop();
    rows.Add(new Row(Path.GetFileName(path), truth, result.Verdict, result.Confidence, sw.Elapsed.TotalMilliseconds));
}

var report = RenderReport(rows, provider.Name, model, pricing, references.Count);
Console.WriteLine(report);
if (outPath is not null)
{
    var full = Path.GetFullPath(outPath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, report, Encoding.UTF8);
    Console.WriteLine($"\n→ 리포트 저장: {full}");
}
return 0;

// ── helpers ──────────────────────────────────────────────────────────────

Dictionary<string, string> ParseArgs(string[] argv)
{
    var map = new Dictionary<string, string>();
    for (var i = 0; i < argv.Length; i++)
    {
        if (!argv[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = argv[i][2..];
        if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            map[key] = argv[++i];
        else
            map[key] = "true";
    }
    return map;
}

// few-shot 기준 이미지 로드 — {dir}/ok, {dir}/ng 에서 라벨당 최대 cap 장. 파일명 정렬로 결정론적.
List<ReferenceImage> LoadReferences(string dir, int cap)
{
    var refs = new List<ReferenceImage>();
    foreach (var (sub, label) in new[] { ("ok", ReferenceLabel.Ok), ("ng", ReferenceLabel.Ng) })
    {
        var path = Path.Combine(dir, sub);
        if (!Directory.Exists(path)) continue;
        foreach (var f in Directory.GetFiles(path)
                     .Where(f => imageExts.Contains(Path.GetExtension(f)))
                     .OrderBy(f => f, StringComparer.Ordinal).Take(cap))
        {
            refs.Add(new ReferenceImage
            {
                Data = File.ReadAllBytes(f),
                Label = label,
                IsPng = Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase),
            });
        }
    }
    return refs;
}

List<(string Path, Verdict? Truth)> DiscoverImages(string dir)
{
    var items = new List<(string, Verdict?)>();
    foreach (var (label, verdict) in new[] { ("ok", Verdict.OK), ("ng", Verdict.NG) })
    {
        var sub = Path.Combine(dir, label);
        if (Directory.Exists(sub))
        {
            items.AddRange(Directory.GetFiles(sub)
                .Where(f => imageExts.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (f, (Verdict?)verdict)));
        }
    }
    // 라벨 없는 flat 이미지
    if (Directory.Exists(dir))
    {
        items.AddRange(Directory.GetFiles(dir)
            .Where(f => imageExts.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, (Verdict?)null)));
    }
    return items;
}

string RenderReport(List<Row> rs, string prov, string mdl, (string Model, double In, double Out)[] price, int refCount)
{
    var latencies = rs.Select(r => r.LatencyMs).OrderBy(x => x).ToList();
    var labeled = rs.Where(r => r.Truth is not null).ToList();
    var p50 = Median(latencies);
    var p95 = latencies[Math.Min(latencies.Count - 1, (int)(latencies.Count * 0.95))];

    var sb = new StringBuilder();
    sb.AppendLine("# VLM 벤치마크 리포트").AppendLine();
    sb.AppendLine($"- provider: `{prov}` / model: `{mdl}`");
    sb.AppendLine($"- 모드: {(refCount > 0 ? $"**few-shot** (기준 이미지 {refCount}장)" : "**zero-shot** (criteria 텍스트만)")}");
    sb.AppendLine($"- 이미지 수: {rs.Count} (라벨됨 {labeled.Count})");
    sb.AppendLine(Inv($"- latency: p50 **{p50:F0} ms**, p95 **{p95:F0} ms**, min {latencies.Min():F0} / max {latencies.Max():F0}"));

    if (prov == "mock")
    {
        sb.AppendLine();
        sb.AppendLine("> ⚠️ **mock provider** — 이미지를 보지 않는다. 아래 정확도/판정은 "
            + "**파이프라인 흐름 시연일 뿐 의미 없음**. 실측은 키 확보 후 "
            + "`--provider openai|google` 로 재실행(= M0.1).");
    }

    if (labeled.Count > 0)
    {
        var tp = labeled.Count(r => r.Truth == Verdict.NG && r.Verdict == Verdict.NG);
        var tn = labeled.Count(r => r.Truth == Verdict.OK && r.Verdict == Verdict.OK);
        var fp = labeled.Count(r => r.Truth == Verdict.OK && r.Verdict == Verdict.NG);
        var fn = labeled.Count(r => r.Truth == Verdict.NG && r.Verdict == Verdict.OK);
        var acc = (double)(tp + tn) / labeled.Count;
        var precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0.0;
        var recall = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0.0;
        var f1 = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0.0;
        sb.AppendLine().AppendLine("## 정확도 (NG = positive)").AppendLine();
        sb.AppendLine(Inv($"- accuracy **{acc:F3}** / precision {precision:F3} / recall {recall:F3} / F1 **{f1:F3}**"));
        sb.AppendLine($"- confusion: TP(NG→NG) {tp}, TN(OK→OK) {tn}, FP(OK→NG) {fp}, FN(NG→OK) {fn}");
    }

    sb.AppendLine().AppendLine("## 건별 결과").AppendLine();
    sb.AppendLine("| 이미지 | 정답 | 판정 | confidence | latency(ms) |");
    sb.AppendLine("|---|---|---|---|---|");
    foreach (var r in rs)
    {
        var truth = r.Truth?.ToString() ?? "-";
        sb.AppendLine(Inv($"| {r.Name} | {truth} | {r.Verdict} | {r.Confidence:F2} | {r.LatencyMs:F0} |"));
    }

    sb.AppendLine().AppendLine("## 비용 참조").AppendLine();
    sb.AppendLine("건별 실 토큰은 provider usage 로 채워야 한다(키 확보 후 TODO). 단가(USD/1M tok):").AppendLine();
    sb.AppendLine("| model | input | output |");
    sb.AppendLine("|---|---|---|");
    foreach (var (m, pin, pout) in price)
    {
        var mark = m == mdl ? " ←" : "";
        sb.AppendLine(Inv($"| {m} | ${pin:F2} | ${pout:F2}{mark} |"));
    }
    if (!price.Any(p => p.Model == mdl))
        sb.AppendLine().AppendLine($"> `{mdl}` 단가 미등록 — 표에 추가 필요.");

    return sb.ToString();
}

static double Median(List<double> sorted)
{
    if (sorted.Count == 0) return 0;
    var mid = sorted.Count / 2;
    return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
}

static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

internal readonly record struct Row(string Name, Verdict? Truth, Verdict Verdict, double Confidence, double LatencyMs);
