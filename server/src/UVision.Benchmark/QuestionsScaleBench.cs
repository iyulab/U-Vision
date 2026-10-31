using System.Diagnostics;
using System.Globalization;
using System.Text;
using HoneAI;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Questions;
using UVision.Api.Storage;

namespace UVision.Benchmark;

/// <summary>
/// 문제 큐가 다음 문제를 고를 때 매번 읽는 세 곳의 시간을 잰다 — 날짜 창의 결정 장부(<see cref="LedgerQuestionCandidateSource"/>),
/// 날짜별 라벨 사이드카 목록, 이미 낸 문제 파일 목록. 합성 데이터를 실제 저장소 클래스로 쓰고(장부는 날짜마다 한 파일,
/// 라벨 · 문제는 건마다 한 파일) 첫 조회와 반복 중앙값을 낸다. 느린 저장소(네트워크 공유)에서 파일 하나 여는 비용이 시간을
/// 정하므로, 읽는 파일 수가 어느 쪽에서 얼마나 자라는지가 결론이다. 파일 캐시를 비울 수 없어 값은 하한이다.
/// </summary>
internal static class QuestionsScaleBench
{
    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        var days = Int(opts, "days", 7);
        var perDay = Int(opts, "per-day", 1000);
        var labeledPerDay = Int(opts, "labeled-per-day", 100);
        var questionCounts = (opts.GetValueOrDefault("questions") ?? "200,1000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        var root = opts.GetValueOrDefault("dir") ?? Path.Combine(Path.GetTempPath(), "uvision-questions-scale-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        var sb = new StringBuilder();
        sb.AppendLine("# 문제 큐 후보 수집의 시간 — 장부 창 · 라벨 목록 · 문제 목록");
        sb.AppendLine();
        sb.AppendLine($"- 합성: 날짜 창 {days}일 × 하루 장부 {perDay}줄(기억 신호 12 키), 하루 라벨 사이드카 {labeledPerDay}건 · 논리 코어 {Environment.ProcessorCount} · {(IsDebug ? "**Debug 빌드 — 시간을 믿지 말 것**" : "Release")} · 데이터 루트 `{(opts.ContainsKey("dir") ? "지정 경로" : "임시 폴더")}`");
        sb.AppendLine("- 한계: 쓴 직후에 쟀다 — 파일 캐시를 비울 수 없어 «첫 조회» 도 따뜻할 수 있다(하한). 반복은 3회 중앙값.");
        sb.AppendLine();
        sb.AppendLine("| 낸 문제 수 | 장부 창(ms) | 라벨 목록 {창 전체}(ms) | 문제 목록(ms) | 합계 첫 조회(ms) | 합계 반복(ms) | 장부 줄 | 라벨 | 문제 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");

        try
        {
            var paths = new StoragePaths(new StorageOptions { DataPath = root }, root);
            const string scenario = "qscale";
            var dates = Enumerable.Range(0, days)
                .Select(i => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();

            var write = Stopwatch.StartNew();
            await WriteLedgerAsync(paths, scenario, dates, perDay);
            await WriteLabelsAsync(paths, scenario, dates, labeledPerDay);
            var ledgerWriteS = write.Elapsed.TotalSeconds;
            Console.Error.WriteLine($"장부 {dates.Count} 파일 + 라벨 {dates.Count * labeledPerDay} 건 쓰기 {ledgerWriteS:F1} 초");

            var questionStore = new FileQuestionStore(paths);
            var labelStore = new FileLabelStore(paths);
            var source = new LedgerQuestionCandidateSource(paths);
            var written = 0;
            foreach (var target in questionCounts.Order())
            {
                for (; written < target; written++)
                    await questionStore.CreateAsync(NewQuestion(scenario, dates[written % dates.Count], $"q_{written:D6}"), default);

                var first = await MeasureAsync(source, labelStore, questionStore, scenario, dates);
                var repeats = new List<Sample>();
                for (var r = 0; r < 3; r++)
                    repeats.Add(await MeasureAsync(source, labelStore, questionStore, scenario, dates));
                var mid = repeats.OrderBy(s => s.Total).ElementAt(1);
                var line = $"| {target} | {first.Ledger:F0} | {first.Labels:F0} | {first.Questions:F0} | {first.Total:F0} | {mid.Total:F0} | {first.LedgerLines} | {first.LabelCount} | {first.QuestionCount} |";
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

    private sealed record Sample(double Ledger, double Labels, double Questions, int LedgerLines, int LabelCount, int QuestionCount)
    {
        public double Total => Ledger + Labels + Questions;
    }

    private static async Task<Sample> MeasureAsync(
        LedgerQuestionCandidateSource source, FileLabelStore labels, FileQuestionStore questions, string scenario, IReadOnlyList<string> dates)
    {
        var clock = Stopwatch.StartNew();
        var candidates = await source.ListAsync(scenario, dates, default);
        var ledgerMs = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        var labelCount = 0;
        foreach (var date in dates)
            labelCount += (await labels.ListAsync(scenario, date, default)).Count;
        var labelsMs = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        var asked = await questions.ListAsync(scenario, default);
        var questionsMs = clock.Elapsed.TotalMilliseconds;
        return new Sample(ledgerMs, labelsMs, questionsMs, candidates.Count, labelCount, asked.Count);
    }

    private static async Task WriteLedgerAsync(StoragePaths paths, string scenario, IReadOnlyList<string> dates, int perDay)
    {
        var rng = new Random(7);
        foreach (var date in dates)
        {
            var sink = new JsonlProvenanceSink(paths.LedgerJsonl(scenario, date));
            for (var i = 0; i < perDay; i++)
            {
                var id = $"img_{date.Replace("-", "", StringComparison.Ordinal)}_{i:D6}";
                var score = rng.NextDouble();
                var record = new PredictionProvenance
                {
                    Kind = JudgeKind.Vlm,
                    SliceKey = "qscale|ok",
                    Path = [],
                    Rationale = "표면에 이상 없음 — 기준 이미지와 비교해 윤곽 · 색 · 위치가 모두 허용 범위 안에 있다고 판단함.",
                    Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["operative:verdict"] = score > 0.95 ? "NG" : "OK",
                        ["operative:kind"] = "vlm",
                        ["memory:neighbors"] = "5",
                        ["memory:nearest_distance"] = (score * 0.4).ToString("0.######", CultureInfo.InvariantCulture),
                        ["memory:agreed_class"] = "OK",
                        ["memory:encoder"] = "dinov2-small:0123456789ab",
                        ["memory:global_score"] = score.ToString("0.######", CultureInfo.InvariantCulture),
                        ["memory:patch_score"] = (score * 0.5).ToString("0.######", CultureInfo.InvariantCulture),
                        ["memory:bank_size"] = "600",
                        ["memory:bank_patches"] = "30000",
                        ["memory:patch_encoder"] = "dinov2-small.layer8",
                        ["origin:kind"] = "field",
                    },
                };
                await sink.AppendAsync(id, record, default);
            }
        }
    }

    private static async Task WriteLabelsAsync(StoragePaths paths, string scenario, IReadOnlyList<string> dates, int labeledPerDay)
    {
        var store = new FileLabelStore(paths); // 실제 저장소로 써서 사이드카 모양이 읽기 쪽과 어긋나지 않게 한다
        foreach (var date in dates)
        {
            for (var i = 0; i < labeledPerDay; i++)
            {
                var id = $"img_{date.Replace("-", "", StringComparison.Ordinal)}_{i:D6}";
                await store.AppendLabelAsync(scenario, date, id, "OK", "bench", default, LabelStream.Learn);
            }
        }
    }

    private static StoredQuestion NewQuestion(string scenario, string date, string imageId) => new()
    {
        QuestionId = Guid.NewGuid().ToString("N"), ScenarioId = scenario, Date = date, ImageId = imageId,
        Form = QuestionForm.Single, Stream = LabelStream.Learn, Reason = QuestionReason.Uncertain, Score = 0.5,
        CreatedAt = "2026-10-31T00:00:00Z", AskedTo = "dev", State = QuestionState.Answered, Answer = "OK", AnsweredBy = "dev",
    };

    private static int Int(Dictionary<string, string> opts, string key, int fallback) =>
        int.TryParse(opts.GetValueOrDefault(key), out var v) && v > 0 ? v : fallback;

    private static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif
}
