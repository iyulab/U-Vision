using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UVision.Benchmark;

/// <summary>
/// 순서 있는 등급(예: 상 &gt; 중 &gt; 하) 판정의 가능성 측정 — 엔진에 판정 유형을 짓기 전에 «모델이 등급을 가르는가» 를 본다.
/// 표본 목록의 <c>train</c> 분할에서 등급마다 기준 이미지를 몇 장 골라(few-shot, <c>--refs-per-class 0</c> 이면 zero-shot) 보여 주고,
/// <c>test</c> 분할을 한 장씩 물어 정확히 맞힘 · 한 단계 이내(±1) 맞힘 · 혼동표 · 등급별 재현율을 낸다. 모델에게는 등급 이름 하나만
/// 답하게 한다(구조화 출력 없음 — 공급자마다 스키마 거절이 다르다). OpenAI 호환 Chat Completions 만 부른다.
/// </summary>
internal static class GradeBench
{
    public static async Task<int> RunAsync(Dictionary<string, string> opts)
    {
        if (DataRoot.Resolve(opts) is not { } dataRoot) return 1;
        if (!opts.TryGetValue("manifest", out var name) || !opts.TryGetValue("order", out var orderSpec))
        {
            Console.Error.WriteLine("grade: --manifest <이름> · --order <좋은 등급부터 쉼표, 예: 상,중,하> 필수 (그리고 --endpoint · --model, 키는 VLM_API_KEY)");
            return 1;
        }
        var order = orderSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var endpoint = (opts.GetValueOrDefault("endpoint") ?? Environment.GetEnvironmentVariable("VLM_ENDPOINT") ?? "").TrimEnd('/');
        var model = opts.GetValueOrDefault("model") ?? "";
        var key = Environment.GetEnvironmentVariable("VLM_API_KEY") ?? "";
        if (endpoint.Length == 0 || model.Length == 0) { Console.Error.WriteLine("grade: --endpoint · --model 필수"); return 1; }

        var refsPerClass = int.TryParse(opts.GetValueOrDefault("refs-per-class"), out var r) && r >= 0 ? r : 2;
        var perClass = int.TryParse(opts.GetValueOrDefault("per-class"), out var pc) && pc > 0 ? pc : 20;
        var concurrency = int.TryParse(opts.GetValueOrDefault("concurrency"), out var cc) && cc > 0 ? cc : 3;
        var maxTokens = int.TryParse(opts.GetValueOrDefault("max-output-tokens"), out var mt) && mt > 0 ? mt : 4096;
        var context = opts.GetValueOrDefault("context") ?? "검사 이미지";
        double? temperature = double.TryParse(opts.GetValueOrDefault("temperature"), CultureInfo.InvariantCulture, out var temp) ? temp : null;

        var samples = SampleManifest.Read(SampleManifest.PathFor(dataRoot, name));
        var test = SampleManifest.Pick(samples, "test", perClass).Where(s => order.Contains(s.Class)).ToList();
        var refs = refsPerClass == 0
            ? []
            : order.SelectMany(grade => EvenlySpaced(samples.Where(s => s.Split == "train" && s.Class == grade).ToList(), refsPerClass)).ToList();
        if (test.Count == 0) { Console.Error.WriteLine("grade: test 분할이 비어 있다"); return 1; }
        foreach (var grade in order.Where(g => refsPerClass > 0 && !refs.Any(x => x.Class == g)))
            Console.Error.WriteLine($"경고: 등급 '{grade}' 의 train 기준 이미지가 없다 — 그 등급은 기준 없이 묻는다");

        var systemPrompt =
            $"당신은 {context}를 보고 등급을 매기는 검사원이다. 등급은 좋은 순서로 {string.Join(" > ", order)} 이다. " +
            $"답은 반드시 등급 이름 하나({string.Join(" · ", order)} 중)만 쓴다. 설명을 붙이지 않는다.";
        // 등급 정의를 글로 준다 — 기준 이미지만으로는 등급 사이의 «무엇이 다른가» 가 암묵적이다.
        var criteriaPath = opts.GetValueOrDefault("criteria");
        if (criteriaPath is not null)
        {
            if (!File.Exists(criteriaPath)) { Console.Error.WriteLine($"grade: --criteria 파일이 없다: {criteriaPath}"); return 1; }
            systemPrompt += "\n\n등급 기준:\n" + File.ReadAllText(criteriaPath, Encoding.UTF8).Trim();
        }

        var refParts = new List<object>();
        foreach (var grade in order)
            foreach (var s in refs.Where(x => x.Class == grade))
            {
                refParts.Add(new { type = "text", text = $"기준 이미지 — 등급: {grade}" });
                refParts.Add(ImagePart(Path.Combine(dataRoot, s.Path)));
            }

        using var http = new HttpClient { BaseAddress = new Uri(endpoint + "/"), Timeout = TimeSpan.FromSeconds(300) };
        if (key.Length > 0) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var results = new (Sample Sample, string? Predicted, double Seconds)[test.Count];
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(); // 실패 이유별 건수(첫 200자) — 실패를 삼키지 않는다
        var gate = new SemaphoreSlim(concurrency);
        var done = 0;
        await Task.WhenAll(test.Select(async (s, i) =>
        {
            await gate.WaitAsync();
            try
            {
                var parts = new List<object>(refParts)
                {
                    new { type = "text", text = refs.Count > 0 ? "위 기준 이미지를 참고해, 아래 이미지의 등급을 답하라." : "아래 이미지의 등급을 답하라." },
                    ImagePart(Path.Combine(dataRoot, s.Path)),
                };
                var request = new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["messages"] = new object[] { new { role = "system", content = systemPrompt }, new { role = "user", content = parts } },
                    ["max_tokens"] = maxTokens,
                };
                // 온도는 기본으로 보내지 않는다 — 일부 공급자(Anthropic 경로)는 temperature 를 400 으로 거절한다.
                if (temperature is { } degrees) request["temperature"] = degrees;
                var body = JsonSerializer.Serialize(request);
                var clock = Stopwatch.StartNew();
                string? predicted = null;
                try
                {
                    using var response = await http.PostAsync("chat/completions", new StringContent(body, Encoding.UTF8, "application/json"));
                    var json = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(json);
                        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                        predicted = Parse(text, order);
                        if (predicted is null) failures.AddOrUpdate("등급 이름이 없는 답: " + Clip(text), 1, (_, n) => n + 1);
                    }
                    else
                        failures.AddOrUpdate($"HTTP {(int)response.StatusCode}: {Clip(json)}", 1, (_, n) => n + 1);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    failures.AddOrUpdate($"{e.GetType().Name}: {Clip(e.Message)}", 1, (_, n) => n + 1);
                }
                results[i] = (s, predicted, clock.Elapsed.TotalSeconds);
            }
            finally
            {
                gate.Release();
                Console.Error.Write($"\r{Interlocked.Increment(ref done)}/{test.Count}");
            }
        }));
        Console.Error.WriteLine();

        var report = Render(name, model, order, refsPerClass, context, results, criteriaPath)
            + (failures.IsEmpty
                ? ""
                : "\n실패 이유:\n\n" + string.Join("\n", failures.OrderByDescending(f => f.Value).Select(f => $"- {f.Value} × {f.Key}")) + "\n");
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

    /// <summary>답 문장에서 등급 이름을 하나 읽는다 — 가장 먼저 나오는 것(없으면 null = 판정 불가).</summary>
    internal static string? Parse(string text, IReadOnlyList<string> order)
    {
        string? best = null;
        var bestAt = int.MaxValue;
        foreach (var grade in order)
        {
            var at = text.IndexOf(grade, StringComparison.Ordinal);
            if (at >= 0 && at < bestAt) { best = grade; bestAt = at; }
        }
        return best;
    }

    internal static (int Exact, int Adjacent, int Judged) Score(IReadOnlyList<(string Truth, string? Predicted)> rows, IReadOnlyList<string> order)
    {
        int exact = 0, adjacent = 0, judged = 0;
        foreach (var (truth, predicted) in rows)
        {
            if (predicted is null) continue;
            judged++;
            var distance = Math.Abs(order.ToList().IndexOf(truth) - order.ToList().IndexOf(predicted));
            if (distance == 0) exact++;
            if (distance <= 1) adjacent++;
        }
        return (exact, adjacent, judged);
    }

    /// <summary>
    /// 아무것도 보지 않는 추측의 기대 정확도 — 등급이 셋이면 한 단계 이내(±1)는 무작위로도 크게 나오므로(예: 78 %) ±1 은 모델이 이미지를
    /// 봤다는 증거가 못 된다. 균등 무작위의 기대값과, 한 등급만 찍을 때 가장 나은 등급의 ±1 을 함께 낸다.
    /// </summary>
    internal static (double RandomExact, double RandomAdjacent, double BestFixedAdjacent, string BestFixedGrade) Baselines(
        IReadOnlyList<string> truths, IReadOnlyList<string> order)
    {
        var k = order.Count;
        var index = order.Select((grade, i) => (grade, i)).ToDictionary(x => x.grade, x => x.i);
        var known = truths.Where(index.ContainsKey).Select(t => index[t]).ToList();
        if (known.Count == 0 || k == 0) return (0, 0, 0, "");
        // 정답 등급마다 «±1 안에 드는 등급 수» / k 가 무작위 추측의 ±1 적중 확률이다.
        var randomAdjacent = known.Average(t => Enumerable.Range(0, k).Count(g => Math.Abs(g - t) <= 1) / (double)k);
        var fixedAdjacent = Enumerable.Range(0, k)
            .Select(g => (Grade: order[g], Rate: known.Count(t => Math.Abs(g - t) <= 1) / (double)known.Count))
            .MaxBy(x => x.Rate);
        return (1.0 / k, randomAdjacent, fixedAdjacent.Rate, fixedAdjacent.Grade);
    }

    private static string Clip(string text) => text.Length <= 200 ? text.ReplaceLineEndings(" ") : text[..200].ReplaceLineEndings(" ");

    private static List<Sample> EvenlySpaced(List<Sample> items, int count) =>
        items.Count <= count ? items : [.. Enumerable.Range(0, count).Select(i => items[(int)((long)i * items.Count / count)])];

    private static object ImagePart(string path) => new
    {
        type = "image_url",
        image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(path)) },
    };

    private static string Render(string name, string model, IReadOnlyList<string> order, int refsPerClass, string context,
        (Sample Sample, string? Predicted, double Seconds)[] results, string? criteriaPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var (exact, adjacent, judged) = Score([.. results.Select(x => (x.Sample.Class, x.Predicted))], order);
        var failed = results.Length - judged;
        var majority = results.GroupBy(x => x.Sample.Class).Max(g => g.Count());
        var seconds = results.Where(x => x.Predicted is not null).Select(x => x.Seconds).Order().ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# 등급 판정 가능성 — 목록 `{name}` test 분할, 모델 `{model}`");
        sb.AppendLine();
        sb.AppendLine($"- {context} · 등급 순서 {string.Join(" > ", order)} · {(refsPerClass == 0 ? "zero-shot" : $"few-shot(등급마다 기준 {refsPerClass}장)")} · 질의 {results.Length}장(판정 {judged} · 실패 {failed}){(criteriaPath is null ? "" : $" · 등급 기준 글 `{Path.GetFileName(criteriaPath)}`")}");
        sb.AppendLine($"- 정확히 맞춤 {exact}/{judged} = {(judged == 0 ? 0 : exact / (double)judged).ToString("P1", inv)} · 한 단계 이내(±1) {adjacent}/{judged} = {(judged == 0 ? 0 : adjacent / (double)judged).ToString("P1", inv)} · 기준선(가장 많은 등급 찍기) {majority}/{results.Length} = {(majority / (double)results.Length).ToString("P1", inv)}");
        var (randomExact, randomAdjacent, fixedAdjacent, fixedGrade) = Baselines([.. results.Select(x => x.Sample.Class)], order);
        sb.AppendLine($"- 아무것도 안 보는 추측 — 균등 무작위 정확 {randomExact.ToString("P1", inv)} · ±1 {randomAdjacent.ToString("P1", inv)}, 한 등급만 찍기(최선 `{fixedGrade}`) ±1 {fixedAdjacent.ToString("P1", inv)} — ±1 은 이 수보다 높아야 의미가 있다");
        if (seconds.Count > 0)
            sb.AppendLine($"- 지연 p50 {seconds[seconds.Count / 2]:F1} s · p95 {seconds[(int)(seconds.Count * 0.95)]:F1} s");
        sb.AppendLine();
        sb.AppendLine("혼동표 (행 = 정답, 열 = 모델 답):");
        sb.AppendLine();
        sb.AppendLine("| 정답 \\ 답 | " + string.Join(" | ", order) + " | 판정 불가 | 재현율 |");
        sb.AppendLine("|---|" + string.Join("|", order.Select(_ => "---")) + "|---|---|");
        foreach (var truth in order)
        {
            var rows = results.Where(x => x.Sample.Class == truth).ToList();
            if (rows.Count == 0) continue;
            var cells = order.Select(p => rows.Count(x => x.Predicted == p).ToString(inv));
            var hit = rows.Count(x => x.Predicted == truth);
            sb.AppendLine($"| {truth} ({rows.Count}) | {string.Join(" | ", cells)} | {rows.Count(x => x.Predicted is null)} | {hit}/{rows.Count} |");
        }
        return sb.ToString();
    }
}
