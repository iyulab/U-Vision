using System.Diagnostics;
using System.Globalization;
using System.Text;
using HoneAI;
using UVision.Api.Services.Memory;
using UVision.Api.Services.Mloop;

namespace UVision.Benchmark;

/// <summary>
/// <c>mloop serve</c> 가 <c>/embed</c> 응답을 보낸 뒤에도 CPU 를 쓰는지, 그래서 같은 기계에서 응답 직후 하는 패치 거리 계산이
/// 느려지는지를 잰다. 반복마다: 서버를 잠시 쉬게 한 뒤 임베딩을 한 번 부르고 — 서버 프로세스의 CPU 시간을 응답 직후 · 0.4 초 뒤 ·
/// 1.5 초 뒤에 읽으며 — 응답 직후 같은 계산(질의 한 장 × 기억 은행)을 돌린다. 이어서 서버가 쉰 뒤 같은 계산을 한 번 더 돌려 비교한다.
/// 기억 은행과 질의 패치는 <c>patches</c> 가 쓰는 벡터 캐시에서 읽는다(임베딩을 새로 부르는 것은 측정용 호출뿐).
/// </summary>
internal static class EmbedCpuBench
{
    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        if (DataRoot.Resolve(opts) is not { } dataRoot) return 1;
        if (!opts.TryGetValue("manifest", out var name) || !int.TryParse(opts.GetValueOrDefault("server-pid"), out var pid))
        {
            Console.Error.WriteLine("embed-cpu: --manifest <이름> 과 --server-pid <mloop serve 프로세스 번호> 필수 (그리고 --endpoint · --encoder 또는 MEMORY_ENDPOINT · MEMORY_ENCODER)");
            return 1;
        }
        var bankSize = int.TryParse(opts.GetValueOrDefault("bank"), out var b) && b > 0 ? b : 600;
        var reps = int.TryParse(opts.GetValueOrDefault("reps"), out var r) && r > 0 ? r : 15;
        var withPatches = !string.Equals(opts.GetValueOrDefault("patches"), "false", StringComparison.OrdinalIgnoreCase);

        var samples = SampleManifest.Read(SampleManifest.PathFor(dataRoot, name));
        var bank = PatchBench.EvenlySpaced(samples.Where(s => s.Split == "train" && s.Class == "OK").ToList(), bankSize);
        var query = samples.First(s => s.Split == "test" && s.Class == "OK");
        var endpoint = opts.GetValueOrDefault("endpoint") ?? Environment.GetEnvironmentVariable("MEMORY_ENDPOINT") ?? "";
        var encoderName = opts.GetValueOrDefault("encoder") ?? Environment.GetEnvironmentVariable("MEMORY_ENCODER") ?? "";
        var client = MloopClient.Create(endpoint, 300, Environment.GetEnvironmentVariable("MEMORY_TOKEN"));

        var (encoder, grid, features) = await PatchBench.LoadAsync(client, encoderName, dataRoot, name, bank.Concat([query]).DistinctBy(s => s.Sha256).ToList());
        var patchCount = grid.Rows * grid.Columns;
        var dim = encoder.Dimension;
        var bankPatches = new float[bank.Count * patchCount * dim];
        for (var i = 0; i < bank.Count; i++)
            features[bank[i].Sha256].Patches.CopyTo(bankPatches, i * patchCount * dim);
        var queryPatches = features[query.Sha256].Patches;
        var queryBytes = await File.ReadAllBytesAsync(Path.Combine(dataRoot, query.Path));

        using var server = Process.GetProcessById(pid);
        TimeSpan Cpu() { server.Refresh(); return server.TotalProcessorTime; }
        double Score() // 질의 한 장의 패치 점수 — 걸린 시간(ms)
        {
            var clock = Stopwatch.StartNew();
            PatchDistance.PatchScore(queryPatches, patchCount, bankPatches, bank.Count * patchCount, dim);
            return clock.Elapsed.TotalMilliseconds;
        }

        Score(); Score(); // 데워 둔다(JIT · 스레드 풀)
        var rest = Cpu();
        await Task.Delay(1000);
        var restMs = (Cpu() - rest).TotalMilliseconds; // 요청이 없을 때 1 초 동안 서버 CPU

        var call = new List<double>(); var during = new List<double>();
        var after04 = new List<double>(); var after15 = new List<double>();
        var immediate = new List<double>(); var idle = new List<double>();
        for (var i = 0; i < reps; i++)
        {
            await Task.Delay(3000); // 서버가 쉴 시간
            var before = Cpu();
            var clock = Stopwatch.StartNew();
            await client.EmbedAsync(new MLoopEmbeddingRequest([queryBytes], encoderName, Patches: withPatches));
            call.Add(clock.Elapsed.TotalMilliseconds);
            var atEnd = Cpu();
            during.Add((atEnd - before).TotalMilliseconds);
            var t04 = Task.Run(async () => { await Task.Delay(400); return Cpu(); });
            var t15 = Task.Run(async () => { await Task.Delay(1500); return Cpu(); });
            immediate.Add(Score());
            after04.Add(((await t04) - atEnd).TotalMilliseconds);
            after15.Add(((await t15) - atEnd).TotalMilliseconds);
            await Task.Delay(1600); // 서버가 쉴 시간 — 같은 계산을 한 번 더 잰다
            idle.Add(Score());
            Console.Error.Write($"\r반복 {i + 1}/{reps}");
        }
        Console.Error.WriteLine();

        static double Median(List<double> v) => v.Order().ElementAt(v.Count / 2);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# 임베딩 응답 뒤 서버 CPU — 목록 `{name}`, 인코더 `{PatchBench.KeyOf(encoder)}`");
        sb.AppendLine();
        sb.AppendLine($"- `/embed{(withPatches ? "?patches=true" : "")}` 질의 한 장 · 기억 은행 {bank.Count}장({bank.Count * patchCount}패치) · 반복 {reps} · 반복 사이 3 초 쉼 · 논리 코어 {Environment.ProcessorCount}");
        sb.AppendLine($"- 요청이 없을 때 서버 CPU(1 초 구간): {restMs:F0} ms");
        sb.AppendLine();
        sb.AppendLine("| 값(중앙값, ms) | 값 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 임베딩 호출 | {Median(call):F0} |");
        sb.AppendLine($"| 서버 CPU — 호출 중 | {Median(during):F0} |");
        sb.AppendLine($"| 서버 CPU — 응답 뒤 0.4 초 | {Median(after04):F0} |");
        sb.AppendLine($"| 서버 CPU — 응답 뒤 1.5 초 | {Median(after15):F0} |");
        sb.AppendLine($"| 패치 점수 계산 — 응답 직후 | {Median(immediate):F0} |");
        sb.AppendLine($"| 패치 점수 계산 — 서버가 쉰 뒤 | {Median(idle):F0} |");
        sb.AppendLine();
        sb.AppendLine("범위(최소–최대): 응답 뒤 0.4 초 CPU " + $"{after04.Min():F0}–{after04.Max():F0} · 직후 계산 {immediate.Min():F0}–{immediate.Max():F0} · 쉰 뒤 계산 {idle.Min():F0}–{idle.Max():F0}");
        var report = sb.ToString();
        Console.WriteLine(report);
        if (opts.GetValueOrDefault("out") is { } outPath)
        {
            var full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, report, new UTF8Encoding(false));
            Console.WriteLine($"→ 리포트 저장: {full}");
        }
        return 0;
    }
}
