using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Benchmark;

/// <summary>
/// 결과 목록 조회(<c>GET /results</c> 의 저장소 쪽)가 하루 건수에 따라 얼마나 걸리는지 잰다 — 파생 색인이 필요한 규모에 닿는지의 증거.
/// 합성 결과 N 건을 실제 <see cref="FileInspectionStore"/> 로 한 날짜 폴더에 쓰고(이미지 + json, 일부는 사람 라벨 사이드카),
/// <c>ListAsync</c> 시간(쓴 직후 첫 조회 · 반복 중앙값)과 응답이 될 JSON 직렬화 시간 · 바이트를 낸다. 한 쪽씩 읽는
/// <c>ListPageAsync</c>(<c>--page</c> 건, 기본 100)의 첫 쪽 · 반복 중앙값 · 전부 쪽으로 순회한 합계도 함께 잰다.
/// 파일 캐시를 비울 수 없어 «콜드 디스크» 는 재지 못한다(쓴 직후라 캐시가 따뜻하다) — 값은 하한이다.
/// </summary>
internal static class ResultsScaleBench
{
    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        var counts = (opts.GetValueOrDefault("counts") ?? "500,1000,2000,5000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray();
        var imageKb = int.TryParse(opts.GetValueOrDefault("image-kb"), out var kb) && kb > 0 ? kb : 150;
        var labelEvery = int.TryParse(opts.GetValueOrDefault("label-every"), out var le) && le > 0 ? le : 10;
        var page = int.TryParse(opts.GetValueOrDefault("page"), out var pg) && pg > 0 ? pg : 100;
        var root = opts.GetValueOrDefault("dir") ?? Path.Combine(Path.GetTempPath(), "uvision-results-scale-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        var image = new byte[imageKb * 1024];
        new Random(1).NextBytes(image);
        var timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var date = StoragePaths.DateBucketOf(timestamp);
        const string scenario = "scale";

        var sb = new StringBuilder();
        sb.AppendLine("# 결과 목록 조회의 규모별 시간 — `FileInspectionStore.ListAsync`");
        sb.AppendLine();
        sb.AppendLine($"- 합성 결과(이미지 {imageKb} KB + json, {labelEvery}건마다 사람 라벨 사이드카) · 한 날짜 폴더 · 논리 코어 {Environment.ProcessorCount} · {(IsDebug ? "**Debug 빌드 — 시간을 믿지 말 것**" : "Release")}");
        sb.AppendLine("- 한계: 파일 캐시를 비울 수 없어 쓴 직후(캐시 따뜻함)에 쟀다 — 콜드 디스크 시간은 이보다 길다(UNVERIFIED).");
        sb.AppendLine();
        sb.AppendLine($"| 건수 | 폴더 파일 수 | 쓰기 | 전체 첫 조회(ms) | 전체 반복 중앙값(ms) | 직렬화(ms) | 응답(MB) | 쪽 {page}건 첫 조회(ms) | 쪽 반복 중앙값(ms) | 쪽으로 전부 순회(ms) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        try
        {
            // 데워 둔다(JIT) — 첫 건수가 JIT 비용을 떠안지 않게.
            await MeasureAsync(Path.Combine(root, "warm"), 30, image, timestamp, date, scenario, labelEvery, page);
            foreach (var n in counts)
            {
                var m = await MeasureAsync(Path.Combine(root, $"n{n}"), n, image, timestamp, date, scenario, labelEvery, page);
                var line = $"| {n} | {m.Files} | {m.WriteSeconds:F1} 초 | {m.First:F0} | {m.Repeat:F0} | {m.Serialize:F0} | {m.Megabytes:F2} | {m.PageFirst:F0} | {m.PageRepeat:F0} | {m.WalkAll:F0} |";
                sb.AppendLine(line);
                Console.Error.WriteLine(line);
            }
        }
        finally
        {
            if (!opts.ContainsKey("keep"))
                try { Directory.Delete(root, recursive: true); } catch (IOException) { /* 임시 폴더 — 남아도 무해 */ }
        }

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

    private sealed record Measurement(
        int Files, double WriteSeconds, double First, double Repeat, double Serialize, double Megabytes,
        double PageFirst, double PageRepeat, double WalkAll);

    private static async Task<Measurement> MeasureAsync(
        string dataPath, int count, byte[] image, string timestamp, string date, string scenario, int labelEvery, int page)
    {
        var paths = new StoragePaths(new StorageOptions { DataPath = dataPath }, dataPath);
        var store = new FileInspectionStore(paths);

        var write = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var id = $"img_{i:D6}";
            await store.SaveAsync(image, "jpg", new StoredResult
            {
                ScenarioId = scenario,
                ImageId = id,
                Verdict = i % 17 == 0 ? Verdict.NG : Verdict.OK,
                Findings = "표면에 이상 없음 — 기준 이미지와 비교해 윤곽 · 색 · 위치가 모두 허용 범위 안에 있다고 판단함. 조명 반사가 일부 있으나 결함으로 보이지 않음.",
                Confidence = 0.93,
                Timestamp = timestamp,
                ImageFile = id + ".jpg",
                DeviceId = "device-1",
                DeviceLabel = "라인 A 입구",
            });
            if (i % labelEvery == 0)
                await File.WriteAllTextAsync(Path.Combine(paths.DateDir(scenario, date), id + ".label.json"), "{\"label\":\"OK\",\"labeled_at\":\"" + timestamp + "\"}");
        }
        write.Stop();

        var clock = Stopwatch.StartNew();
        var first = await store.ListAsync(scenario, date);
        var firstMs = clock.Elapsed.TotalMilliseconds;
        if (first.Count != count) throw new InvalidOperationException($"조회 {first.Count} 건 ≠ 쓴 {count} 건");

        var repeats = new List<double>();
        for (var r = 0; r < 3; r++)
        {
            clock.Restart();
            await store.ListAsync(scenario, date);
            repeats.Add(clock.Elapsed.TotalMilliseconds);
        }

        clock.Restart();
        var json = JsonSerializer.Serialize(first, StoragePaths.Json);
        var serializeMs = clock.Elapsed.TotalMilliseconds;

        // 한 쪽씩 — 첫 쪽(쓴 직후 첫 호출) · 반복 중앙값 · 커서를 따라 전부 순회한 합계(쪽마다 디렉터리 목록을 다시 읽는다).
        clock.Restart();
        var firstPage = await store.ListPageAsync(scenario, date, page, cursor: null);
        var pageFirstMs = clock.Elapsed.TotalMilliseconds;
        var pageRepeats = new List<double>();
        for (var r = 0; r < 3; r++)
        {
            clock.Restart();
            await store.ListPageAsync(scenario, date, page, cursor: null);
            pageRepeats.Add(clock.Elapsed.TotalMilliseconds);
        }
        clock.Restart();
        var walked = 0;
        string? cursor = null;
        do
        {
            var next = await store.ListPageAsync(scenario, date, page, cursor);
            walked += next.Items.Count;
            cursor = next.NextCursor;
        } while (cursor is not null);
        var walkMs = clock.Elapsed.TotalMilliseconds;
        if (walked != count || firstPage.Items.Count != Math.Min(page, count))
            throw new InvalidOperationException($"쪽 순회 {walked} 건 ≠ 쓴 {count} 건");

        var files = Directory.EnumerateFiles(paths.DateDir(scenario, date)).Count();
        return new Measurement(
            files, write.Elapsed.TotalSeconds, firstMs, repeats.Order().ElementAt(1), serializeMs, Encoding.UTF8.GetByteCount(json) / 1048576.0,
            pageFirstMs, pageRepeats.Order().ElementAt(1), walkMs);
    }

    private static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif
}
