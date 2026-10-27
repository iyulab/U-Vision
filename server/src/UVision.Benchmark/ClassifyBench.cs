using System.Diagnostics;
using System.Globalization;
using UVision.Api.Configuration;
using UVision.Api.Services.Ml;

namespace UVision.Benchmark;

/// <summary>
/// 전용 ML 분류기 정확도 — 표본 목록의 분할을 지키며 잰다. <c>--export &lt;폴더&gt;</c> 는 train 분할을 MLoop 이미지 분류 학습
/// 폴더(<c>&lt;폴더&gt;/&lt;클래스&gt;/</c>)로 복사하고(원본 무변경), <c>--endpoint</c> 는 test 분할을 운영 분류기(<see cref="MloopClassifier"/>,
/// <c>mloop serve</c> 의 <c>/predict</c>)로 질의해 혼동표 · 불량 재현율을 낸다. 학습(<c>mloop train</c>)은 그 사이에 사람이 · 스크립트가 돌린다.
/// <para>
/// 학습과 시험을 같은 목록의 서로 다른 분할에서 가져오는 것이 요점이다 — 같은 이미지를 학습하고 시험하면 정확도가 1.0 으로 나온다(누수).
/// 목록이 이미 같은 내용 · 분할 간 겹침을 뺐다(<see cref="SampleManifest.Dedupe"/>).
/// </para>
/// </summary>
internal static class ClassifyBench
{
    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        if (DataRoot.Resolve(opts) is not { } dataRoot) return 1;
        if (!opts.TryGetValue("manifest", out var name))
        {
            Console.Error.WriteLine("classify: --manifest <이름> 필수, 그리고 --export <학습 폴더> 또는 --endpoint <mloop serve 주소>");
            return 1;
        }
        var samples = SampleManifest.Read(SampleManifest.PathFor(dataRoot, name));
        if (opts.TryGetValue("export", out var exportDir))
            return Export(dataRoot, samples, exportDir, int.TryParse(opts.GetValueOrDefault("per-class"), out var cap) && cap > 0 ? cap : int.MaxValue);
        if (opts.TryGetValue("endpoint", out var endpoint)) return await EvaluateAsync(dataRoot, name, samples, endpoint, opts);
        Console.Error.WriteLine("classify: --export <학습 폴더> 또는 --endpoint <mloop serve 주소> 중 하나");
        return 1;
    }

    internal static int Export(string dataRoot, List<Sample> samples, string exportDir, int perClass)
    {
        // 클래스마다 고르게 perClass 장까지 — 학습이 너무 오래 걸리는 큰 목록(수천 장)을 줄이려는 레버. 기본은 전부.
        var train = SampleManifest.Pick(samples, "train", perClass);
        if (train.Count == 0)
        {
            Console.Error.WriteLine("train 분할 0장 — 분할이 없는 목록은 학습/시험으로 나눌 수 없다(manifest --split-csv 또는 분할 폴더로 다시 지을 것)");
            return 1;
        }
        foreach (var s in train)
        {
            var dir = Path.Combine(exportDir, s.Class);
            Directory.CreateDirectory(dir);
            // 파일명은 내용 해시 — 원본 이름은 클래스 사이 · 하위 폴더 사이에서 겹칠 수 있다.
            var target = Path.Combine(dir, s.Sha256[..16] + Path.GetExtension(s.Path).ToLowerInvariant());
            if (!File.Exists(target)) File.Copy(Path.Combine(dataRoot, s.Path), target);
        }
        foreach (var g in train.GroupBy(s => s.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"train\t{g.Key}\t{g.Count()}");
        Console.WriteLine($"→ {Path.GetFullPath(exportDir)} ({train.Count}장)");
        return 0;
    }

    private static async Task<int> EvaluateAsync(
        string dataRoot, string name, List<Sample> samples, string endpoint, Dictionary<string, string> opts)
    {
        var perClass = int.TryParse(opts.GetValueOrDefault("per-class"), out var pc) && pc > 0 ? pc : int.MaxValue;
        var test = SampleManifest.Pick(samples, "test", perClass);
        // mloop serve 는 분당 호출 수를 제한한다(429) — 한도 안으로 간격을 둔다. 지정이 없으면 간격 없음.
        var gap = int.TryParse(opts.GetValueOrDefault("max-per-minute"), out var rate) && rate > 0
            ? TimeSpan.FromMinutes(1.0 / rate) : TimeSpan.Zero;
        if (test.Count == 0)
        {
            Console.Error.WriteLine("test 분할 0장");
            return 1;
        }
        var classifier = MlClassifierFactory.Create(new MlOptions
        {
            Provider = "mloop",
            Endpoint = endpoint,
            Model = opts.GetValueOrDefault("model", "default"),
            Token = Environment.GetEnvironmentVariable("ML_TOKEN") ?? "",
            TimeoutSeconds = 300,
        });

        var tally = new Dictionary<(string Truth, string Predicted), int>();
        var latencies = new List<double>();
        var failed = 0;
        foreach (var (s, i) in test.Select((s, i) => (s, i)))
        {
            if (i > 0 && gap > TimeSpan.Zero) await Task.Delay(gap);
            var image = await File.ReadAllBytesAsync(Path.Combine(dataRoot, s.Path));
            var sw = Stopwatch.StartNew();
            try
            {
                var r = await classifier.ClassifyAsync(image, "bench");
                latencies.Add(sw.Elapsed.TotalMilliseconds);
                tally[(s.Class, r.Label)] = tally.GetValueOrDefault((s.Class, r.Label)) + 1;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"\n{s.Path}: {ex.Message}");
            }
            Console.Error.Write($"\r분류 {i + 1}/{test.Count}");
        }
        Console.Error.WriteLine();

        Console.WriteLine(Render(name, test, tally, latencies, failed));
        return failed == 0 ? 0 : 3;
    }

    internal static string Render(
        string name, IReadOnlyList<Sample> test, IReadOnlyDictionary<(string Truth, string Predicted), int> tally,
        IReadOnlyList<double> latenciesMs, int failed)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# ML 분류 정확도 — 목록 `{name}` test 분할");
        sb.AppendLine();
        var judged = tally.Values.Sum();
        sb.AppendLine($"- 시험 {test.Count}장 — 분류 {judged} · 실패 {failed}");
        if (latenciesMs.Count > 0)
        {
            var sorted = latenciesMs.OrderBy(x => x).ToList();
            sb.AppendLine(string.Create(inv, $"- 지연: p50 {sorted[sorted.Count / 2]:F0} ms · max {sorted[^1]:F0} ms (직렬)"));
        }
        var classes = test.Select(s => s.Class).Concat(tally.Keys.Select(k => k.Predicted)).Distinct().Order(StringComparer.Ordinal).ToList();
        sb.AppendLine().AppendLine("| 정답 \\ 예측 | " + string.Join(" | ", classes) + " |");
        sb.AppendLine("|---|" + string.Concat(classes.Select(_ => "---|")));
        foreach (var t in classes.Where(c => test.Any(s => s.Class == c)))
            sb.AppendLine($"| {t} | " + string.Join(" | ", classes.Select(p => tally.GetValueOrDefault((t, p)).ToString(inv))) + " |");
        var correct = tally.Where(x => x.Key.Truth == x.Key.Predicted).Sum(x => x.Value);
        if (judged > 0) sb.AppendLine().AppendLine(string.Create(inv, $"- 정확도 {(double)correct / judged:F3} ({correct}/{judged})"));
        // 불량 재현율 — must-catch 쪽. NG 를 OK 로 넘긴 수가 운영에서 비싸다.
        var ng = tally.Where(x => x.Key.Truth == "NG").Sum(x => x.Value);
        if (ng > 0)
        {
            var caught = tally.GetValueOrDefault(("NG", "NG"));
            sb.AppendLine(string.Create(inv, $"- 불량 재현율 {(double)caught / ng:F3} ({caught}/{ng}) · 불량을 OK 로 {tally.GetValueOrDefault(("NG", "OK"))}장"));
        }
        var ok = tally.Where(x => x.Key.Truth == "OK").Sum(x => x.Value);
        if (ok > 0)
            sb.AppendLine(string.Create(inv, $"- 정상 오경보 {tally.GetValueOrDefault(("OK", "NG"))}/{ok}"));
        return sb.ToString();
    }
}
