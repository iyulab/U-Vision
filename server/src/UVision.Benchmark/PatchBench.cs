using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HoneAI;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Mloop;

namespace UVision.Benchmark;

/// <summary>
/// 패치 특징 이상 점수 재생 — VLM 없이. 표본 목록의 <c>train</c> 정상 사진 일부를 기억(메모리 뱅크)으로, <c>test</c> 분할을 질의로
/// 두고, 같은 <c>POST /embed?patches=true</c> 응답의 두 표현으로 이미지 이상 점수를 낸다:
/// <list type="bullet">
/// <item><b>전역</b> — 이미지당 벡터 하나, 기억 사진 전역 벡터까지의 최근접 코사인 거리(지금 기억 단의 점수).</item>
/// <item><b>패치</b> — 패치마다 기억 패치 전부까지의 최근접 코사인 거리, 이미지 점수 = 패치 점수의 최댓값(PatchCore 관행).</item>
/// </list>
/// 문턱에 기대지 않고 비교한다 — 정상 시험 사진의 오경보율을 같게 맞췄을 때 통과(놓친) 불량 수와 AUROC.
/// 벡터는 <c>GEN/u-vision/patches/{목록}/{인코더 판}/{해시}.bin</c> 에 캐시한다(같은 판이면 다시 재지 않는다).
/// </summary>
internal static class PatchBench
{
    private const int Batch = 8; // 패치 모드는 한 요청 최대 8장(MLoop 0.46.0)
    private static readonly double[] FalseAlarmRates = [0.01, 0.05, 0.10];

    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        if (DataRoot.Resolve(opts) is not { } dataRoot) return 1;
        if (!opts.TryGetValue("manifest", out var name))
        {
            Console.Error.WriteLine("patches: --manifest <이름> 필수 (그리고 --endpoint · --encoder 또는 MEMORY_ENDPOINT · MEMORY_ENCODER)");
            return 1;
        }
        var samples = SampleManifest.Read(SampleManifest.PathFor(dataRoot, name));
        var bankSize = int.TryParse(opts.GetValueOrDefault("bank"), out var b) && b > 0 ? b : 200;
        var perClass = int.TryParse(opts.GetValueOrDefault("per-class"), out var pc) && pc > 0 ? pc : int.MaxValue;

        var bank = EvenlySpaced(samples.Where(s => s.Split == "train" && s.Class == "OK").ToList(), bankSize);
        var test = SampleManifest.Pick(samples, "test", perClass);
        if (bank.Count == 0 || test.Count == 0)
        {
            Console.Error.WriteLine("patches: train 정상 또는 test 분할이 비어 있다(분할이 있는 목록 필요 — manifest --split-csv)");
            return 1;
        }

        var endpoint = opts.GetValueOrDefault("endpoint") ?? Environment.GetEnvironmentVariable("MEMORY_ENDPOINT") ?? "";
        var encoderName = opts.GetValueOrDefault("encoder") ?? Environment.GetEnvironmentVariable("MEMORY_ENCODER") ?? "";
        var client = MloopClient.Create(endpoint, 300, Environment.GetEnvironmentVariable("MEMORY_TOKEN"));

        // --bank-out: 기억으로 쓴 사진을 폴더에 복사한다(`mloop train` 이 같은 사진으로 은행을 짓게 — 표본을 맞추는 길).
        if (opts.GetValueOrDefault("bank-out") is { } bankOut)
        {
            Directory.CreateDirectory(bankOut);
            foreach (var (b0, i) in bank.Select((b0, i) => (b0, i)))
                File.Copy(Path.Combine(dataRoot, b0.Path), Path.Combine(bankOut, $"{i:D4}-{b0.Sha256[..8]}{Path.GetExtension(b0.Path)}"), overwrite: true);
            Console.Error.WriteLine($"기억 사진 {bank.Count}장 → {Path.GetFullPath(bankOut)}");
        }

        var needed = bank.Concat(test).DistinctBy(s => s.Sha256).ToList();
        var (encoder, grid, features) = await LoadAsync(client, encoderName, dataRoot, name, needed);
        var patchCount = grid.Rows * grid.Columns;
        var dim = encoder.Dimension;

        var bankPatches = new float[bank.Count * patchCount * dim];
        for (var i = 0; i < bank.Count; i++)
            features[bank[i].Sha256].Patches.CopyTo(bankPatches, i * patchCount * dim);
        var bankGlobal = bank.Select(s => features[s.Sha256].Global).ToList();

        var normalGlobal = new List<double>();
        var defectGlobal = new List<double>();
        var normalPatch = new List<double>();
        var defectPatch = new List<double>();
        foreach (var (s, i) in test.Select((s, i) => (s, i)))
        {
            var f = features[s.Sha256];
            var g = PatchDistance.GlobalScore(f.Global, bankGlobal);
            var p = PatchDistance.PatchScore(f.Patches, patchCount, bankPatches, bank.Count * patchCount, dim);
            (s.Class == "OK" ? normalGlobal : defectGlobal).Add(g);
            (s.Class == "OK" ? normalPatch : defectPatch).Add(p);
            Console.Error.Write($"\r점수 {i + 1}/{test.Count}");
        }
        Console.Error.WriteLine();

        // --anomaly-endpoint: MLoop `image-anomaly` 모델(`mloop serve`)의 점수를 같은 질의로 받아 세 번째 줄로 비교한다.
        AnomalyScores? anomaly = null;
        if (opts.GetValueOrDefault("anomaly-endpoint") is { } anomalyEndpoint)
            anomaly = await ScoreWithAnomalyModelAsync(anomalyEndpoint, dataRoot, test);

        var report = Render(name, encoder, grid, bank.Count, normalGlobal, defectGlobal, normalPatch, defectPatch, anomaly);
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

    private static List<Sample> EvenlySpaced(List<Sample> items, int count)
    {
        if (items.Count <= count) return items;
        return [.. Enumerable.Range(0, count).Select(i => items[(int)((long)i * items.Count / count)])];
    }

    private sealed record Features(float[] Global, float[] Patches);

    private static async Task<(MLoopEncoder Encoder, MLoopPatchGrid Grid, Dictionary<string, Features> Features)> LoadAsync(
        IMLoopClient client, string encoderName, string dataRoot, string name, IReadOnlyList<Sample> samples)
    {
        // 인코더 판과 격자를 먼저 안다 — 캐시 폴더 이름이 그 판이다. 첫 장은 캐시에 올려 둔다.
        var first = samples[0];
        var probe = await client.EmbedAsync(new MLoopEmbeddingRequest(
            [await File.ReadAllBytesAsync(Path.Combine(dataRoot, first.Path))], encoderName, Patches: true));
        var encoder = probe.Encoder;
        var grid = probe.Grid ?? throw new InvalidOperationException("mloop /embed?patches=true 가 격자를 주지 않았다(0.46.0 미만?)");
        if (encoder.Preprocessor is null)
            throw new InvalidOperationException("encoder.preprocessor 가 없다 — 판 키를 알 수 없다(MLoop 0.46.0+ 필요)");
        var dim = encoder.Dimension;
        var expected = (1 + grid.Rows * grid.Columns) * dim;
        var dir = Path.Combine(dataRoot, "GEN", "u-vision", "patches", name, KeyOf(encoder));
        Directory.CreateDirectory(dir);

        var map = new Dictionary<string, Features>(StringComparer.Ordinal);
        Store(dir, first, probe, 0, dim);
        var missing = new List<Sample>();
        foreach (var s in samples)
        {
            if (s.Sha256 == first.Sha256) { map[s.Sha256] = Read(dir, s, dim, expected)!; continue; }
            var cached = Read(dir, s, dim, expected);
            if (cached is null) missing.Add(s); else map[s.Sha256] = cached;
        }

        for (var i = 0; i < missing.Count; i += Batch)
        {
            var chunk = missing.Skip(i).Take(Batch).ToList();
            var bytes = new List<ReadOnlyMemory<byte>>();
            foreach (var s in chunk) bytes.Add(await File.ReadAllBytesAsync(Path.Combine(dataRoot, s.Path)));
            var result = await client.EmbedAsync(new MLoopEmbeddingRequest(bytes, encoderName, Patches: true));
            if (KeyOf(result.Encoder) != KeyOf(encoder))
                throw new InvalidOperationException($"측정 중 인코더 판이 바뀌었다: {KeyOf(encoder)} → {KeyOf(result.Encoder)}");
            for (var j = 0; j < chunk.Count; j++)
            {
                Store(dir, chunk[j], result, j, dim);
                map[chunk[j].Sha256] = Read(dir, chunk[j], dim, expected)!;
            }
            Console.Error.Write($"\r임베딩 {Math.Min(i + Batch, missing.Count)}/{missing.Count}");
        }
        if (missing.Count > 0) Console.Error.WriteLine();
        return (encoder, grid, map);
    }

    // 판 키 = (id, sha256, preprocessor) — 셋이 모두 같을 때만 벡터가 비교 가능하다.
    internal static string KeyOf(MLoopEncoder e) => $"{e.Id}-{e.Sha256?[..12]}-{e.Preprocessor}".Replace('/', '_');

    private static string FileFor(string dir, Sample s) => Path.Combine(dir, s.Sha256[..16] + ".bin");

    private static void Store(string dir, Sample s, MLoopEmbedding embedding, int index, int dim)
    {
        var patches = embedding.Patches?[index].Vectors
            ?? throw new InvalidOperationException("패치 없이 답했다");
        var floats = new float[(1 + patches.Count) * dim];
        embedding.Vectors[index].CopyTo(floats, 0);
        for (var p = 0; p < patches.Count; p++) patches[p].CopyTo(floats, (1 + p) * dim);
        var path = FileFor(dir, s);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, MemoryMarshal.AsBytes<float>(floats).ToArray());
        File.Move(temp, path, overwrite: true);
    }

    private static Features? Read(string dir, Sample s, int dim, int expectedFloats)
    {
        var path = FileFor(dir, s);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != expectedFloats * sizeof(float)) return null; // 끊긴 파일은 다시 잰다
        var floats = new float[expectedFloats];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return new Features(floats[..dim], floats[dim..]);
    }

    private sealed record AnomalyScores(List<double> Normal, List<double> Defect, double MedianMilliseconds);

    /// <summary>
    /// <c>mloop serve</c> 의 <c>POST /predict</c>(image-anomaly) 로 질의 사진 점수를 받는다 — 한 장씩 보내 장당 지연을 잰다.
    /// 응답의 인코더 판이 우리 쪽 재생과 같은지는 확인하지 않는다(판은 호출자가 맞추고 리포트에 적는다).
    /// </summary>
    private static async Task<AnomalyScores> ScoreWithAnomalyModelAsync(string endpoint, string dataRoot, IReadOnlyList<Sample> test)
    {
        using var http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        var token = Environment.GetEnvironmentVariable("ANOMALY_TOKEN") ?? Environment.GetEnvironmentVariable("MEMORY_TOKEN");
        if (!string.IsNullOrEmpty(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var normal = new List<double>();
        var defect = new List<double>();
        var millis = new List<double>();
        foreach (var (s, i) in test.Select((s, i) => (s, i)))
        {
            var body = JsonSerializer.Serialize(new { images = new[] { Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(dataRoot, s.Path))) } });
            var clock = Stopwatch.StartNew();
            using var response = await http.PostAsync("predict", new StringContent(body, Encoding.UTF8, "application/json"));
            var json = await response.Content.ReadAsStringAsync();
            clock.Stop();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"mloop /predict {(int)response.StatusCode}: {json[..Math.Min(300, json.Length)]}");
            using var doc = JsonDocument.Parse(json);
            var score = doc.RootElement.GetProperty("images")[0].GetProperty("score").GetDouble();
            (s.Class == "OK" ? normal : defect).Add(score);
            millis.Add(clock.Elapsed.TotalMilliseconds);
            Console.Error.Write($"\rimage-anomaly 점수 {i + 1}/{test.Count}");
        }
        Console.Error.WriteLine();
        return new AnomalyScores(normal, defect, millis.Order().ElementAt(millis.Count / 2));
    }

    private static string Render(
        string name, MLoopEncoder encoder, MLoopPatchGrid grid, int bankImages,
        List<double> normalGlobal, List<double> defectGlobal, List<double> normalPatch, List<double> defectPatch,
        AnomalyScores? anomaly = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# 패치 특징 이상 점수 재생 — 목록 `{name}` test 분할");
        sb.AppendLine();
        sb.AppendLine($"- 인코더 `{KeyOf(encoder)}` · 격자 {grid.Rows}×{grid.Columns}(패치 {grid.PatchSize}px · crop {grid.CropSize}) · 기억 = train 정상 {bankImages}장");
        sb.AppendLine($"- 시험 {normalGlobal.Count + defectGlobal.Count}장 — 정상 {normalGlobal.Count} · 불량 {defectGlobal.Count}");
        sb.AppendLine();
        sb.AppendLine("| 점수 | AUROC | " + string.Join(" | ", FalseAlarmRates.Select(r => $"오경보 {r:P0} 에서 통과한 불량")) + " |");
        sb.AppendLine("|---|---|" + string.Join("|", FalseAlarmRates.Select(_ => "---")) + "|");
        var rows = new List<(string, List<double>, List<double>)>
        {
            ("전역 벡터(지금 기억 단)", normalGlobal, defectGlobal),
            ("패치 최댓값", normalPatch, defectPatch),
        };
        if (anomaly is not null)
            rows.Add(($"MLoop image-anomaly(질의 중앙값 {anomaly.MedianMilliseconds:F0} ms/장, HTTP 포함)", anomaly.Normal, anomaly.Defect));
        foreach (var (label, normal, defect) in rows)
        {
            var missed = FalseAlarmRates.Select(r => $"{PatchScoring.MissedAtFalseAlarm(normal, defect, r)}/{defect.Count}");
            sb.AppendLine(string.Create(inv, $"| {label} | {PatchScoring.Auroc(normal, defect):F3} | {string.Join(" | ", missed)} |"));
        }
        return sb.ToString();
    }
}

/// <summary>패치 재생의 비교 지표 — 문턱에 기대지 않는다(점수 계산은 <see cref="PatchDistance"/>, 장부와 같은 것).</summary>
internal static class PatchScoring
{
    /// <summary>불량 점수가 정상 점수보다 높을 확률(동점은 절반) — 순위만 보므로 점수의 단위와 무관하다.</summary>
    public static double Auroc(IReadOnlyList<double> normal, IReadOnlyList<double> defect)
    {
        if (normal.Count == 0 || defect.Count == 0) return double.NaN;
        var wins = 0.0;
        foreach (var d in defect)
            foreach (var n in normal)
                wins += d > n ? 1 : d == n ? 0.5 : 0;
        return wins / ((double)normal.Count * defect.Count);
    }

    /// <summary>정상 오경보가 <paramref name="falseAlarmRate"/> 이하가 되는 가장 낮은 문턱에서, 그 문턱 이하(= OK 로 통과)인 불량 수.</summary>
    public static int MissedAtFalseAlarm(IReadOnlyList<double> normal, IReadOnlyList<double> defect, double falseAlarmRate)
    {
        if (normal.Count == 0) return defect.Count;
        var sorted = normal.Order().ToList();
        // 문턱 t 보다 큰 정상이 오경보다 — 허용 오경보 수만큼 위쪽을 남기고 그 아래 최댓값을 문턱으로.
        var allowed = (int)Math.Floor(falseAlarmRate * sorted.Count);
        var threshold = sorted[sorted.Count - 1 - allowed];
        return defect.Count(d => d <= threshold);
    }
}
