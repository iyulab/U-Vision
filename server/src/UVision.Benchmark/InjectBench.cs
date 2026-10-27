using System.Globalization;
using Microsoft.Kiota.Abstractions;
using UVision.Client;
using UVision.Client.Models;
using CaptureOrigin = UVision.Api.Models.CaptureOrigin;

namespace UVision.Benchmark;

/// <summary>
/// 시험 데이터셋 주입 — 표본 목록의 이미지를 실행 중인 엔진에 `POST /v2/inspect` 로 넣는다(`UVision.Client`). 판정은 엔진이
/// 평소대로 내고 기록하며, 요청에 출처(`origin=dataset` · `origin_ref=<목록>:<내용 해시 앞 16자>`)를 붙여 현장 촬영과 섞이지
/// 않게 한다. <c>--label</c> 이면 정답 클래스를 사람 라벨로 심는다(라벨러 <c>dataset:&lt;목록&gt;</c>) — 학습 · 기억 · 관문이
/// 이 라벨로 돈다.
/// </summary>
internal static class InjectBench
{
    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        if (DataRoot.Resolve(opts) is not { } dataRoot) return 1;
        if (!opts.TryGetValue("manifest", out var name) || !opts.TryGetValue("scenario", out var scenarioId)
            || !opts.TryGetValue("engine", out var engineUrl))
        {
            Console.Error.WriteLine("inject: --manifest <이름> · --scenario <id> · --engine <엔진 주소> 필수");
            return 1;
        }
        var split = opts.GetValueOrDefault("split");
        // --label: 판정 직후 정답을 사람 라벨로 심는다(기억에 바로 들어간다). --label-after: 한 묶음을 다 넣은 뒤에 심는다 —
        // 질의가 자기 묶음의 이전 질의를 기억으로 보지 않으면서 정답을 남긴다(재생용 시험 묶음).
        var labelAfter = opts.ContainsKey("label-after");
        var label = opts.ContainsKey("label") || labelAfter;
        var deferred = new List<LabelInput>();
        var limit = int.TryParse(opts.GetValueOrDefault("limit"), out var n) ? n : int.MaxValue;
        var all = SampleManifest.Read(SampleManifest.PathFor(dataRoot, name));
        // --per-class N: 클래스마다 목록 전체에서 고르게 N 장(--offset 으로 다른 자리) 뽑아 클래스가 섞인 순서로 넣는다 — 목록 앞에서
        // 자르면(--limit 만) 클래스별로 정렬된 목록의 앞쪽 한 클래스와 이웃 사진으로 쏠린다.
        var samples = int.TryParse(opts.GetValueOrDefault("per-class"), out var perClass) && perClass > 0
            ? SampleManifest.PickInterleaved(
                all, split, perClass, int.TryParse(opts.GetValueOrDefault("offset"), out var offset) ? offset : 0)
                .Take(limit).ToList()
            : all.Where(s => split is null || s.Split == split).Take(limit).ToList();
        if (samples.Count == 0)
        {
            Console.Error.WriteLine($"주입할 표본 0장 (목록 {name}, 분할 {split ?? "전체"})");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var engine = UVisionClient.Create(http, new Uri(engineUrl));
        var tally = new Dictionary<(string Truth, string Decided), int>();
        int failed = 0, labeled = 0;
        foreach (var (s, i) in samples.Select((s, i) => (s, i)))
        {
            var form = new MultipartBody();
            await using var image = File.OpenRead(Path.Combine(dataRoot, s.Path));
            form.AddOrReplacePart("image", ContentTypeOf(s.Path), image, Path.GetFileName(s.Path));
            form.AddOrReplacePart("scenario_id", "text/plain", scenarioId);
            form.AddOrReplacePart("origin", "text/plain", "dataset");
            form.AddOrReplacePart("origin_ref", "text/plain", $"{name}:{s.Sha256[..16]}");
            try
            {
                var result = await engine.V2.Inspect.PostAsync(form)
                    ?? throw new InvalidOperationException("빈 응답");
                var decided = result.Class ?? "-";
                tally[(s.Class, decided)] = tally.GetValueOrDefault((s.Class, decided)) + 1;
                if (label && result.DecisionId is { } id && result.Timestamp is { Length: >= 10 } ts)
                {
                    var input = new LabelInput
                    {
                        ScenarioId = scenarioId, Date = ts[..10], ImageId = id, Label = s.Class, By = CaptureOrigin.LabelerPrefix + name,
                    };
                    if (labelAfter) deferred.Add(input);
                    else { await engine.Results.Label.PutAsync(input); labeled++; }
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"\n{s.Path}: {ex.Message}");
            }
            Console.Error.Write($"\r주입 {i + 1}/{samples.Count}");
        }
        Console.Error.WriteLine();
        foreach (var input in deferred)
        {
            await engine.Results.Label.PutAsync(input);
            labeled++;
        }

        Console.WriteLine($"# 주입 요약 — 목록 `{name}` → 시나리오 `{scenarioId}`");
        Console.WriteLine();
        Console.WriteLine($"- 주입 {samples.Count - failed}장 · 실패 {failed}장 · 정답 라벨 {labeled}장 (출처 origin=dataset)");
        Console.WriteLine();
        Console.WriteLine("| 정답 | 판정 | 장수 |");
        Console.WriteLine("|---|---|---|");
        foreach (var ((truth, decided), count) in tally.OrderBy(t => t.Key.Truth, StringComparer.Ordinal).ThenBy(t => t.Key.Decided, StringComparer.Ordinal))
            Console.WriteLine($"| {truth} | {decided} | {count} |");
        var agree = tally.Where(t => t.Key.Truth == t.Key.Decided).Sum(t => t.Value);
        var judged = tally.Values.Sum();
        if (judged > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"\n정답과 같은 판정 {agree}/{judged} ({(double)agree / judged:P1})"));
        return failed == 0 ? 0 : 3;
    }

    private static string ContentTypeOf(string path) =>
        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
}
