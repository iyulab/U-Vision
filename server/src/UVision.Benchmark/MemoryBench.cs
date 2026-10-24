using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Scenarios;

namespace UVision.Benchmark;

/// <summary>
/// 기억 단 문턱 오프라인 재생 — VLM 없이. 표본 목록의 <c>train</c> 분할을 사람 확정 사례로 기억에 넣고, <c>test</c> 분할을
/// 질의해 엔진과 같은 판정(<see cref="MemoryJudge"/>)과 재생(<see cref="MemoryReplay"/>)으로 문턱마다 응답 수 · 정답 일치율 ·
/// 불량을 OK 로 통과시킨 수를 센다. 정답 클래스가 사람 라벨 자리에 들어간다(교사 = VLM 은 없다).
/// <para>
/// 벡터는 루트의 <c>GEN/u-vision/vectors/{목록}/{인코더 판}.jsonl</c> 에 캐시한다 — 같은 목록 · 같은 인코더 판이면 다시 재지 않는다.
/// </para>
/// </summary>
internal static class MemoryBench
{
    private static readonly double[] WideLadder = [0.02, 0.05, 0.075, 0.10, 0.15, 0.20, 0.25, 0.30, 0.40, 0.50];
    private const int Batch = 16;

    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        if (DataRoot.Resolve(opts) is not { } dataRoot) return 1;
        if (!opts.TryGetValue("manifest", out var name))
        {
            Console.Error.WriteLine("--manifest <이름> 필수 (GEN/u-vision/manifests/<이름>.tsv)");
            return 1;
        }
        var samples = SampleManifest.Read(SampleManifest.PathFor(dataRoot, name));
        var defaults = new MemoryOptions();
        var neighbors = int.TryParse(opts.GetValueOrDefault("neighbors"), out var k) ? k : defaults.Neighbors;
        var configured = double.TryParse(opts.GetValueOrDefault("max-distance"), NumberStyles.Float, CultureInfo.InvariantCulture, out var md)
            ? md : defaults.MaxDistance;

        var embedder = ImageEmbedderFactory.Create(new MemoryOptions
        {
            Provider = "mloop",
            Endpoint = opts.GetValueOrDefault("endpoint") ?? Environment.GetEnvironmentVariable("MEMORY_ENDPOINT") ?? "",
            Encoder = opts.GetValueOrDefault("encoder") ?? Environment.GetEnvironmentVariable("MEMORY_ENCODER") ?? "",
            Token = Environment.GetEnvironmentVariable("MEMORY_TOKEN") ?? "",
            TimeoutSeconds = 300,
        });

        var (encoder, vectors, embedSeconds, embedded) = await EmbedAllAsync(embedder, dataRoot, name, samples);
        // test 분할이 없거나 --leave-one-out 이면 한 장씩 빼고 나머지 전부를 기억으로 — 작은 세트를 둘로 쪼개 더 작게 만들지 않는다.
        var leaveOneOut = opts.ContainsKey("leave-one-out") || samples.All(s => s.Split != "test");
        var train = leaveOneOut ? samples : samples.Where(s => s.Split == "train").ToList();
        var test = leaveOneOut ? samples : samples.Where(s => s.Split == "test").ToList();
        var memory = train.Select(s => new MemoryCase(s.Path, s.Class, vectors[s.Path])).ToList();

        var scenario = EffectiveScenario.Resolve(
            new Scenario { ScenarioId = "bench", Name = "bench" }, mlEnabled: false, memoryEnabled: true);
        // 문턱 2(코사인 거리의 최대)로 판정해 이웃이 모인 클래스와 최근접 거리를 얻고, 문턱은 재생이 가른다 — 장부 재생과 같은 길.
        var answers = test
            .Select(s => (Sample: s, Answer: MemoryJudge.Decide(
                vectors[s.Path],
                leaveOneOut ? memory.Where(c => c.ImageId != s.Path).ToList() : memory,
                neighbors,
                maxDistance: 2.0)))
            .ToList();
        var cases = answers
            .Where(a => a.Answer.NearestDistance is not null)
            .Select(a => new ReplayCase(a.Answer.AgreedClass, a.Answer.NearestDistance!.Value, TeacherClass: null, HumanLabel: a.Sample.Class))
            .ToList();
        var rows = MemoryReplay.Run(cases, scenario, configured, WideLadder);

        var report = Render(name, encoder, neighbors, leaveOneOut, scenario, train, test, answers, rows, embedSeconds, embedded);
        Console.WriteLine(report);
        if (opts.GetValueOrDefault("out") is { } outPath)
        {
            var full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, report, new UTF8Encoding(false));
            Console.WriteLine($"\n→ 리포트 저장: {full}");
        }
        return 0;
    }

    private static async Task<(EncoderIdentity Encoder, Dictionary<string, float[]> Vectors, double Seconds, int Embedded)> EmbedAllAsync(
        IImageEmbedder embedder, string dataRoot, string name, IReadOnlyList<Sample> samples)
    {
        // 인코더 판을 먼저 안다 — 캐시 파일 이름이 그 판이다.
        var first = samples[0];
        var probe = await embedder.EmbedAsync([await File.ReadAllBytesAsync(Path.Combine(dataRoot, first.Path))]);
        var encoder = probe.Encoder;
        var cachePath = Path.Combine(dataRoot, "GEN", "u-vision", "vectors", name, encoder.Key + ".jsonl");
        var vectors = ReadCache(cachePath);
        var missing = samples.Where(s => !vectors.ContainsKey(s.Path)).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var sw = Stopwatch.StartNew();
        await using (var cache = new StreamWriter(cachePath, append: true, new UTF8Encoding(false)))
        {
            for (var i = 0; i < missing.Count; i += Batch)
            {
                var chunk = missing.Skip(i).Take(Batch).ToList();
                var bytes = new List<ReadOnlyMemory<byte>>();
                foreach (var s in chunk) bytes.Add(await File.ReadAllBytesAsync(Path.Combine(dataRoot, s.Path)));
                var result = await embedder.EmbedAsync(bytes);
                if (result.Encoder.Key != encoder.Key)
                    throw new InvalidOperationException($"측정 중 인코더 판이 바뀌었다: {encoder.Key} → {result.Encoder.Key}");
                for (var j = 0; j < chunk.Count; j++)
                {
                    vectors[chunk[j].Path] = result.Vectors[j];
                    await cache.WriteLineAsync(JsonSerializer.Serialize(new { path = chunk[j].Path, vector = result.Vectors[j] }));
                }
                await cache.FlushAsync();
                Console.Error.Write($"\r임베딩 {Math.Min(i + Batch, missing.Count)}/{missing.Count}");
            }
        }
        if (missing.Count > 0) Console.Error.WriteLine();
        return (encoder, vectors, sw.Elapsed.TotalSeconds, missing.Count);
    }

    private static Dictionary<string, float[]> ReadCache(string path)
    {
        var map = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadLines(path))
        {
            // 끊긴 마지막 줄(중단된 측정)은 건너뛴다 — 다음 실행이 그 장을 다시 잰다.
            try
            {
                using var doc = JsonDocument.Parse(line);
                map[doc.RootElement.GetProperty("path").GetString()!] =
                    [.. doc.RootElement.GetProperty("vector").EnumerateArray().Select(e => e.GetSingle())];
            }
            catch (JsonException) { }
        }
        return map;
    }

    private static string Render(
        string name, EncoderIdentity encoder, int neighbors, bool leaveOneOut, EffectiveScenario scenario, List<Sample> train, List<Sample> test,
        List<(Sample Sample, MemoryAnswer Answer)> answers, IReadOnlyList<MemoryReplayRow> rows, double embedSeconds, int embedded)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 기억 단 문턱 재생 리포트 (오프라인)").AppendLine();
        sb.AppendLine(leaveOneOut
            ? $"- 표본 목록: `{name}` — **leave-one-out** {test.Count}장 [{Counts(test)}] (한 장씩 빼고 나머지를 기억으로)"
            : $"- 표본 목록: `{name}` — 기억(train) {train.Count}장 [{Counts(train)}] · 질의(test) {test.Count}장 [{Counts(test)}]");
        sb.AppendLine("- 표본은 내용 해시로 중복을 뺐다(같은 파일 한 장 · train 과 겹친 test 제외).");
        sb.AppendLine($"- 인코더: `{encoder.Key}` (차원 {encoder.Dimension}) · 이웃 k = {neighbors}");
        sb.AppendLine(embedded > 0
            ? Inv($"- 이번에 잰 임베딩: {embedded}장 · {embedSeconds:F0}초 ({embedded / Math.Max(embedSeconds, 0.001):F1}장/초)")
            : "- 이번에 잰 임베딩: 0장 (전부 캐시)");
        sb.AppendLine($"- 이웃이 갈려 어느 문턱에서도 기권: {answers.Count(a => a.Answer.AgreedClass is null)}장");
        sb.AppendLine("- 판정 규칙은 엔진과 같다(`MemoryJudge` · `MemoryReplay`). 정답 클래스가 사람 라벨 자리에 들어가고, 교사(VLM)는 없다.").AppendLine();

        sb.AppendLine("## 최근접 거리 분포 (질의 정답 클래스별)").AppendLine();
        sb.AppendLine("| 정답 | n | p10 | p50 | p90 | max |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var g in answers.Where(a => a.Answer.NearestDistance is not null).GroupBy(a => a.Sample.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var d = g.Select(a => a.Answer.NearestDistance!.Value).Order().ToList();
            sb.AppendLine(Inv($"| {g.Key} | {d.Count} | {Q(d, 0.1):F4} | {Q(d, 0.5):F4} | {Q(d, 0.9):F4} | {d[^1]:F4} |"));
        }

        sb.AppendLine().AppendLine("## 문턱별").AppendLine();
        // 놓침률(엔진 재생과 같은 값)의 분모는 «답한 수»라 OK 가 많은 세트에서 작아 보인다. 현장이 묻는 것은 «불량 가운데 몇 장이
        // OK 로 지나가나» — 질의의 불량 수를 분모로 한 열을 함께 보인다.
        var defects = test.Count(s => scenario.Classes.Any(c => c.MustCatch && c.Id == s.Class));
        sb.AppendLine("놓침 = 기억이 OK 로 답했는데 정답이 불량. 응답률 = 답한 수 / 질의 수. 놓침률 = 놓침 / 답한 수(엔진 재생과 같은 값) · "
            + $"불량 통과율 = 놓침 / 질의의 불량 {defects}장.").AppendLine();
        sb.AppendLine("| 문턱 | 답함 | 응답률 | 정답 일치 | 놓침 | 놓침률 | 불량 통과율 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            var mark = r.Configured ? " ←설정" : "";
            var coverage = (double)r.Answered / Math.Max(test.Count, 1);
            var passed = defects == 0 ? "—" : Inv($"{(double)r.Missed / defects:P1}");
            sb.AppendLine(Inv($"| {r.MaxDistance:0.###}{mark} | {r.Answered} | {coverage:P1} | {Rate(r.LabelAgreement)} | {r.Missed} | {Rate(r.MissRate)} | {passed} |"));
        }
        return sb.ToString();
    }

    private static string Counts(List<Sample> s) =>
        string.Join(" · ", s.GroupBy(x => x.Class).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));

    private static double Q(List<double> sorted, double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];

    private static string Rate(double? r) => r is { } v ? v.ToString("P1", CultureInfo.InvariantCulture) : "—";

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
