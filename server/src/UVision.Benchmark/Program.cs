using System.Diagnostics;
using System.Globalization;
using System.Text;
using UVision.Api.Configuration;
using UVision.Api.Imaging;
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
//       --out ./vlm-benchmark.md
//   # 표본 목록에서 — --images-dir 대신 데이터 루트의 목록을(클래스 OK/NG 만), 클래스마다 고르게 --per-class 장
//   UVISION_DATA_ROOT=... VLM_API_KEY=... VLM_ENDPOINT=... dotnet run --project src/UVision.Benchmark -- \
//       --manifest <이름> [--split test] [--per-class 20] --provider openai-compatible --model <모델> [--concurrency 4]
//   # 표본 목록 짓기 — 데이터 루트의 {dir}/{분할}/{클래스}/ 를 GEN/u-vision/manifests/{name}.tsv 로(원본 무변경)
//   UVISION_DATA_ROOT=... dotnet run --project src/UVision.Benchmark -- manifest --name <이름> //       --dir <루트 상대 폴더> --splits 학습=train,테스트=test --classes 정상=OK,불량=NG
//       분할을 데이터셋의 CSV 가 정하면 --split-csv <dir 기준 CSV> [--object <대상>] — 예: VisA
//       --dir PUB/visa/VisA_20220922 --split-csv split_csv/2cls_highshot.csv --object pcb1 --splits train=train,test=test --classes normal=OK,anomaly=NG
//       (같은 내용의 파일은 한 장으로, train 과 겹친 test 는 뺀다. 분할이 없는 데이터는 --splits Images=all —
//        memory 가 leave-one-out 으로 잰다)
//   # 기억 단 문턱 오프라인 재생 — train 을 기억에, test 를 질의로(MLoop /embed, VLM 없음)
//   UVISION_DATA_ROOT=... MEMORY_ENDPOINT=http://localhost:5417 MEMORY_ENCODER=dinov2-small MEMORY_TOKEN=... //       dotnet run --project src/UVision.Benchmark -- memory --manifest <이름> [--neighbors 5] [--out report.md]
//   # COCO 상자 → 부품 크롭 표본 목록(한 장에 부품 여럿 — 부품 한 개 = 표본 한 장). Training/Validation 을 --append 로 한 목록에
//   UVISION_DATA_ROOT=... dotnet run --project src/UVision.Benchmark -- crops --name <이름> --coco-dir <주석 폴더;…> //       --image-dir <이미지 폴더;…> --split train --classes 정상_역삽=OK,불량_역삽=NG [--pad 0.1] [--min-side 24] [--append]
//   # 패치 특징 이상 점수 재생 — train 정상 일부를 기억으로, test 를 질의로(MLoop 0.46.0+ /embed?patches=true, VLM 없음).
//   # 전역 벡터 점수와 같은 정상 오경보율에서 놓친 불량 수 · AUROC 를 비교
//   UVISION_DATA_ROOT=... MEMORY_ENDPOINT=http://localhost:5417 MEMORY_ENCODER=dinov2-small MEMORY_TOKEN=... \
//       dotnet run --project src/UVision.Benchmark -- patches --manifest <이름> [--bank 200] [--per-class N] [--out report.md]
//   #   --bank-out <폴더> 기억 사진을 복사 → `mloop train <폴더> --task image-anomaly` 로 같은 사진의 은행을 짓고(MLoop 0.47.0+),
//   #   --anomaly-endpoint http://localhost:<port> (ANOMALY_TOKEN) 로 그 모델의 점수를 세 번째 줄로 비교
//   # 전용 ML 분류기 정확도 — train 분할을 MLoop 학습 폴더로 내보내고(mloop train 은 그 폴더에서), test 분할을 /predict 로
//   UVISION_DATA_ROOT=... dotnet run --project src/UVision.Benchmark -- classify --manifest <이름> --export <프로젝트>/datasets/images
//   UVISION_DATA_ROOT=... ML_TOKEN=... dotnet run --project src/UVision.Benchmark -- classify --manifest <이름> --endpoint http://localhost:5417 [--model default] [--per-class N] [--max-per-minute 55]
//   # 리포트 폴더 → 판정원 비교표(VLM · ML 리포트를 다시 읽는다 — 손으로 옮기지 않는다)
//   dotnet run --project src/UVision.Benchmark -- summary --dir <리포트 폴더>[;<폴더>…]
//   # 반복 일관성 — 같은 설정으로 두 번 돌린 VLM 리포트를 짝지어 잡음(답이 움직임) · 체계적 오류(같은 오답)를 가른다
//   dotnet run --project src/UVision.Benchmark -- repeat --a <리포트1.md> --b <리포트2.md> [--out report.md]
//   # 장부 줄 재생 — 엔진이 쌓은 결정 장부의 전역 · 패치 이상 점수를 사람 라벨과 이어 기억 크기별로 비교(읽기 전용)
//   dotnet run --project src/UVision.Benchmark -- ledger-replay --data <엔진 데이터 경로> --scenario <id> [--out report.md]
//   # 시험 데이터셋 주입 — 표본을 실행 중인 엔진의 /v2/inspect 로(origin=dataset), --label 이면 정답을 사람 라벨로 심음
//   UVISION_DATA_ROOT=... dotnet run --project src/UVision.Benchmark -- inject --manifest <이름> --scenario <id> \
//       --engine http://localhost:8080 [--split test] [--limit N | --per-class N [--offset K]] [--label]

const string DefaultCriteria =
    "제품 표면에 긁힘, 이물질, 균열, 솔더 브릿지 등 외관 결함이 없어야 한다. " +
    "결함이 보이면 NG, 깨끗하면 OK.";

var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

// 리포트는 한글이라 콘솔 코드페이지와 무관하게 UTF-8 로 낸다(파이프 · 리다이렉트로 파일에 저장해도 깨지지 않게).
Console.OutputEncoding = new UTF8Encoding(false);

// 개발 편의: .env 로딩(VLM_API_KEY 등).
DotNetEnv.Env.TraversePath().Load();

var opts = ParseArgs(args);

// 하위 명령 — 측정 데이터 루트(UVISION_DATA_ROOT)를 쓰는 VLM 없는 측정. 없으면 아래 VLM 벤치마크.
switch (args.FirstOrDefault())
{
    case "manifest":
        return BuildManifest(opts);
    case "memory":
        return await UVision.Benchmark.MemoryBench.RunAsync(opts);
    case "inject":
        return await UVision.Benchmark.InjectBench.RunAsync(opts);
    case "classify":
        return await UVision.Benchmark.ClassifyBench.RunAsync(opts);
    case "patches":
        return await UVision.Benchmark.PatchBench.RunAsync(opts);
    case "crops":
        return UVision.Benchmark.CocoCrops.Run(opts, Pairs);
    case "summary":
        return UVision.Benchmark.ReportSummary.Run(opts);
    case "repeat":
        return UVision.Benchmark.RepeatBench.Run(opts);
    case "ledger-replay":
        return await UVision.Benchmark.LedgerReplay.RunAsync(opts);
}

List<(string Name, string Path, Verdict? Truth)> images;
if (opts.TryGetValue("manifest", out var manifestName))
{
    if (UVision.Benchmark.DataRoot.Resolve(opts) is not { } dataRoot) return 1;
    var perClass = int.TryParse(opts.GetValueOrDefault("per-class"), out var pc) && pc > 0 ? pc : int.MaxValue;
    var picked = UVision.Benchmark.SampleManifest.Pick(
        UVision.Benchmark.SampleManifest.Read(UVision.Benchmark.SampleManifest.PathFor(dataRoot, manifestName)),
        opts.GetValueOrDefault("split"), perClass);
    // 이 벤치마크는 OK/NG 판정을 잰다 — 그 밖 클래스(유형 · 품번)는 건너뛴다.
    images = [.. picked
        .Where(x => Enum.TryParse<Verdict>(x.Class, out _))
        .Select(x => (x.Path, Path.Combine(dataRoot, x.Path), (Verdict?)Enum.Parse<Verdict>(x.Class)))];
}
else if (opts.TryGetValue("images-dir", out var imagesDir))
{
    images = [.. DiscoverImages(imagesDir).Select(x => (Path.GetFileName(x.Path), x.Path, x.Truth))];
}
else
{
    Console.Error.WriteLine("이미지를 찾지 못함: --images-dir 또는 --manifest 필수");
    return 1;
}
var concurrency = int.TryParse(opts.GetValueOrDefault("concurrency"), out var cc) && cc > 0 ? cc : 1;
var providerName = opts.GetValueOrDefault("provider", "mock");
var model = opts.GetValueOrDefault("model", "gpt-4o");
var criteria = opts.GetValueOrDefault("criteria", DefaultCriteria);
var outPath = opts.GetValueOrDefault("out");
// openai-compatible 서버 주소 — --endpoint 우선, 없으면 VLM_ENDPOINT 환경변수.
var endpoint = opts.GetValueOrDefault("endpoint")
    ?? Environment.GetEnvironmentVariable("VLM_ENDPOINT") ?? "";
// few-shot 기준 이미지: --refs-dir/{ok,ng} 에서 로드(라벨당 최대 --refs-cap, 기본 3). 누수 방지 위해
// 평가셋(--images-dir)과 다른 디렉토리(예: 학습셋)를 지정할 것. 미지정 시 zero-shot.
// --refs-manifest <이름>: 표본 목록의 train 분할에서 클래스마다 --refs-cap 장을 고르게 — 평가는 test 에서 하므로 겹치지 않는다.
// --refs-offset <k>: 그 자리를 목록 안에서 k 만큼 밀어 다른 기준 이미지를 얻는다(기준 이미지 선택에 얼마나 민감한지 잴 때).
var refsDir = opts.GetValueOrDefault("refs-dir");
var refsManifest = opts.GetValueOrDefault("refs-manifest");
var refsCap = int.TryParse(opts.GetValueOrDefault("refs-cap"), out var rc) ? rc : 3;
// --downscale <maxdim>: 설정 시 query·refs 를 longest-side maxdim 으로 축소 후 JPEG 재인코딩(측정 전용).
// 0/미설정이면 원본 그대로(다운스케일 없음). latency/recall 절충 실험용.
var downscale = int.TryParse(opts.GetValueOrDefault("downscale"), out var ds) && ds > 0 ? ds : 0;
List<ReferenceImage> references;
if (refsManifest is not null)
{
    if (UVision.Benchmark.DataRoot.Resolve(opts) is not { } refsRoot) return 1;
    references = [.. UVision.Benchmark.SampleManifest.Pick(
            UVision.Benchmark.SampleManifest.Read(UVision.Benchmark.SampleManifest.PathFor(refsRoot, refsManifest)), "train", refsCap,
            int.TryParse(opts.GetValueOrDefault("refs-offset"), out var refsOffset) && refsOffset > 0 ? refsOffset : 0)
        .Where(x => x.Class is "OK" or "NG")
        .Select(x => new ReferenceImage
        {
            Data = File.ReadAllBytes(Path.Combine(refsRoot, x.Path)),
            Label = x.Class == "OK" ? ReferenceLabel.Ok : ReferenceLabel.Ng,
            IsPng = Path.GetExtension(x.Path).Equals(".png", StringComparison.OrdinalIgnoreCase),
        })];
}
else
{
    references = refsDir is null ? [] : LoadReferences(refsDir, refsCap);
}
if (downscale > 0)
{
    references = references
        .Select(r => new ReferenceImage { Data = ImageDownscaler.Downscale(r.Data, downscale), Label = r.Label, NgLabel = r.NgLabel, IsPng = false })
        .ToList();
}

if (images.Count == 0)
{
    Console.Error.WriteLine("판정할 이미지 0장");
    return 1;
}

var vlmOptions = new VlmOptions
{
    Provider = providerName,
    Model = model,
    ApiKey = Environment.GetEnvironmentVariable("VLM_API_KEY") ?? "",
    Endpoint = endpoint,
    // 생각하는 모델은 어려운 장면에서 기본 4096 을 추론에 다 쓴다 — 측정은 --max-output-tokens(또는 VLM_MAX_OUTPUT_TOKENS)로 올린다.
    ThinkingEffort = opts.GetValueOrDefault("thinking-effort") ?? Environment.GetEnvironmentVariable("VLM_THINKING_EFFORT"),
    MaxOutputTokens = int.TryParse(opts.GetValueOrDefault("max-output-tokens")
        ?? Environment.GetEnvironmentVariable("VLM_MAX_OUTPUT_TOKENS"), out var mot) ? mot : new VlmOptions().MaxOutputTokens,
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

// 건별 실패(시간 초과 · 응답 형태 · 서버 오류)는 그 건만 실패로 남기고 측정을 이어 간다 — 수백 장 측정이 한 건에 죽지 않게.
// 실패는 정확도 분모에서 빠지고 리포트에 따로 센다(조용히 버리지 않는다).
using var gate = new SemaphoreSlim(concurrency);
var done = 0;
var rows = (await Task.WhenAll(images.Select(async item =>
{
    await gate.WaitAsync();
    try
    {
        var data = await File.ReadAllBytesAsync(item.Path);
        if (downscale > 0)
        {
            data = ImageDownscaler.Downscale(data, downscale);
        }
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await provider.InspectAsync(data, scenario);
            return new Row(item.Name, item.Truth, result.Verdict, result.Confidence, sw.Elapsed.TotalMilliseconds, result.Findings, null);
        }
        catch (Exception ex)
        {
            return new Row(item.Name, item.Truth, null, 0, sw.Elapsed.TotalMilliseconds, null, ex.GetType().Name + ": " + ex.Message);
        }
    }
    finally
    {
        gate.Release();
        Console.Error.Write($"\r판정 {Interlocked.Increment(ref done)}/{images.Count}");
    }
}))).ToList();
Console.Error.WriteLine();

var report = RenderReport(rows, provider.Name, model, references.Count, downscale, concurrency, opts.GetValueOrDefault("manifest"));
Console.WriteLine(report);
if (outPath is not null)
{
    var full = Path.GetFullPath(outPath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, report, Encoding.UTF8);
    Console.WriteLine($"\n→ 리포트 저장: {full}");
}
return rows.Any(r => r.Error is not null) ? 3 : 0;

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

int BuildManifest(Dictionary<string, string> o)
{
    if (UVision.Benchmark.DataRoot.Resolve(o) is not { } root) return 1;
    if (!o.TryGetValue("name", out var name) || !o.TryGetValue("dir", out var dir)
        || !o.TryGetValue("splits", out var splits) || !o.TryGetValue("classes", out var classes))
    {
        Console.Error.WriteLine("manifest: --name · --dir · --splits(폴더 또는 CSV 값=train|test,…) · --classes(폴더 또는 CSV 값=클래스,…) 필수"
            + " — 선택 --split-csv <dir 기준 CSV> [--object <대상>]");
        return 1;
    }
    // --split-csv: 폴더가 아니라 데이터셋의 분할 CSV 가 분할을 정한다(값=train|test 사상, --object 로 대상 하나만).
    var build = o.TryGetValue("split-csv", out var splitCsv)
        ? UVision.Benchmark.SampleManifest.FromSplitCsv(root, dir, splitCsv, o.GetValueOrDefault("object"), Pairs(splits), Pairs(classes))
        : UVision.Benchmark.SampleManifest.FromFolders(root, dir, Pairs(splits), Pairs(classes));
    var samples = build.Samples;
    if (samples.Count == 0)
    {
        Console.Error.WriteLine($"표본 0장: {Path.Combine(root, dir)}");
        return 1;
    }
    var path = UVision.Benchmark.SampleManifest.PathFor(root, name);
    UVision.Benchmark.SampleManifest.Write(path, samples);
    foreach (var g in samples.GroupBy(x => (x.Split, x.Class)).OrderBy(g => g.Key))
        Console.WriteLine($"{g.Key.Split}	{g.Key.Class}	{g.Count()}");
    Console.WriteLine($"→ {path} ({samples.Count}장 — 같은 내용 {build.DuplicatesDropped}장 · train 과 겹친 test {build.LeaksDropped}장 뺌)");
    return 0;
}

static Dictionary<string, string> Pairs(string spec) =>
    spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.Split('=', 2))
        .ToDictionary(p => p[0], p => p.Length == 2 ? p[1] : throw new ArgumentException($"'폴더=값' 꼴이 아니다: {p[0]}"));

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

string RenderReport(List<Row> all, string prov, string mdl, int refCount, int downscaleDim, int parallel, string? manifest = null)
{
    var failed = all.Where(r => r.Error is not null).ToList();
    var rs = all.Where(r => r.Error is null).ToList();
    var latencies = rs.Select(r => r.LatencyMs).DefaultIfEmpty(0).OrderBy(x => x).ToList();
    var labeled = rs.Where(r => r.Truth is not null).ToList();
    var p50 = Median(latencies);
    var p95 = latencies[Math.Min(latencies.Count - 1, (int)(latencies.Count * 0.95))];

    var sb = new StringBuilder();
    sb.AppendLine("# VLM 벤치마크 리포트").AppendLine();
    sb.AppendLine($"- provider: `{prov}` / model: `{mdl}`");
    // 표본 목록 = 시나리오 열의 키 — `summary` 가 판정원 × 목록 표를 낼 때 다시 읽는다(파일 이름 규약에 기대지 않는다).
    if (!string.IsNullOrEmpty(manifest)) sb.AppendLine($"- 목록: `{manifest}`");
    sb.AppendLine($"- 모드: {(refCount > 0 ? $"**few-shot** (기준 이미지 {refCount}장)" : "**zero-shot** (criteria 텍스트만)")}");
    sb.AppendLine($"- 다운스케일: {(downscaleDim > 0 ? $"longest-side **{downscaleDim}px** (JPEG q85)" : "없음(원본)")}");
    sb.AppendLine($"- 이미지 수: {all.Count} — 판정 {rs.Count} (라벨됨 {labeled.Count}) · **실패 {failed.Count}**");
    sb.AppendLine(Inv($"- latency(동시 {parallel}): p50 **{p50:F0} ms**, p95 **{p95:F0} ms**, min {latencies.Min():F0} / max {latencies.Max():F0}"));

    if (prov == "mock")
    {
        sb.AppendLine();
        sb.AppendLine("> ⚠️ **mock provider** — 이미지를 보지 않는다. 아래 정확도/판정은 "
            + "**파이프라인 흐름 시연일 뿐 의미 없음**. 실측은 키 확보 후 "
            + "`--provider openai-compatible|openai|google` 로 재실행.");
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
    sb.AppendLine("| 이미지 | 정답 | 판정 | confidence | latency(ms) | 근거 |");
    sb.AppendLine("|---|---|---|---|---|---|");
    foreach (var r in all)
    {
        var truth = r.Truth?.ToString() ?? "-";
        var verdict = r.Verdict?.ToString() ?? "**실패**";
        sb.AppendLine(Inv($"| {r.Name} | {truth} | {verdict} | {r.Confidence:F2} | {r.LatencyMs:F0} | {Cell(r.Error ?? r.Findings ?? "")} |"));
    }

    return sb.ToString();
}

static double Median(List<double> sorted)
{
    if (sorted.Count == 0) return 0;
    var mid = sorted.Count / 2;
    return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
}

static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

static string Cell(string text) => UVision.Benchmark.ReportText.Cell(text);

internal readonly record struct Row(string Name, Verdict? Truth, Verdict? Verdict, double Confidence, double LatencyMs, string? Findings, string? Error);
