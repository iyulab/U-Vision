using System.Globalization;
using System.Text;
using HoneAI;
using UVision.Api.Configuration;
using UVision.Api.Storage;

namespace UVision.Benchmark;

/// <summary>
/// 장부 줄 재생 — 엔진이 쌓은 결정 장부(<c>memory:global_score</c> · <c>memory:patch_score</c> · <c>memory:bank_size</c>)와 사람 라벨을
/// 이미지 id 로 이어, 전역 점수와 패치 점수 중 어느 쪽이 그 시나리오의 불량을 더 잘 가르는지를 <b>기억 크기별로</b> 낸다.
/// 문턱에 기대지 않는 지표(AUROC · 같은 정상 오경보율에서 놓친 불량 수)라 두 점수의 단위가 달라도 같은 기준에서 비교된다.
/// <para>
/// 정답은 사람 라벨이다 — OK 가 정상, 그 밖의 라벨은 불량. 라벨이 없는 건은 정답을 몰라 뺀다. 점수는 판정 <b>당시</b>의 기억으로
/// 낸 값이라(<c>bank_size</c> 가 그때 크기) 기억이 자라는 동안 쌓인 줄을 크기별로 나눠 볼 수 있다. 읽기 전용이다.
/// </para>
/// </summary>
internal static class LedgerReplay
{
    internal sealed record ScoredCase(double Global, double Patch, int BankSize, bool Defect);

    private static readonly double[] FalseAlarmRates = [0.01, 0.05, 0.10];

    /// <summary>기억 크기 구간의 아래 경계(포함). 마지막은 «그 이상».</summary>
    private static readonly int[] BankEdges = [1, 10, 50, 200];

    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        if (!o.TryGetValue("data", out var data) || !o.TryGetValue("scenario", out var scenarioId))
        {
            Console.Error.WriteLine("ledger-replay: --data <엔진 데이터 경로> · --scenario <id> 필수 — 선택 --out <리포트.md>");
            return 1;
        }
        var ledgerDir = Path.Combine(data, scenarioId, "ledger");
        if (!Directory.Exists(ledgerDir))
        {
            Console.Error.WriteLine($"ledger-replay: 장부 폴더가 없다 — {ledgerDir}");
            return 1;
        }

        var paths = new StoragePaths(new StorageOptions { DataPath = data }, AppContext.BaseDirectory);
        var labelStore = new FileLabelStore(paths);
        var cases = new List<ScoredCase>();
        int ledgerLines = 0, unscored = 0, unlabeled = 0;
        foreach (var file in Directory.EnumerateFiles(ledgerDir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            var date = Path.GetFileNameWithoutExtension(file);
            var labels = (await labelStore.ListAsync(scenarioId, date))
                .Where(l => l.OperativeLabel is not null)
                .ToDictionary(l => l.ImageId, l => l.OperativeLabel!, StringComparer.Ordinal);
            await foreach (var record in new JsonlProvenanceSink(file).ReadAsync())
            {
                ledgerLines++;
                var a = record.Provenance.Annotations;
                if (a is null || !TryNumber(a, "memory:global_score", out var global) || !TryNumber(a, "memory:patch_score", out var patch)
                    || !a.TryGetValue("memory:bank_size", out var size) || !int.TryParse(size, CultureInfo.InvariantCulture, out var bank))
                {
                    unscored++;
                    continue;
                }
                if (!labels.TryGetValue(record.SubjectId, out var label))
                {
                    unlabeled++;
                    continue;
                }
                cases.Add(new ScoredCase(global, patch, bank, Defect: !string.Equals(label, "OK", StringComparison.OrdinalIgnoreCase)));
            }
        }

        var report = Render(scenarioId, ledgerLines, unscored, unlabeled, cases);
        Console.WriteLine(report);
        if (o.TryGetValue("out", out var outPath))
            File.WriteAllText(outPath, report, new UTF8Encoding(true));
        return 0;
    }

    private static bool TryNumber(IReadOnlyDictionary<string, string> a, string key, out double value)
    {
        value = 0;
        return a.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    internal static string Render(string scenarioId, int ledgerLines, int unscored, int unlabeled, IReadOnlyList<ScoredCase> cases)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# 패치 · 전역 이상 점수 재생 — 시나리오 `{scenarioId}` (장부 줄)");
        sb.AppendLine();
        sb.AppendLine($"- 장부 {ledgerLines}줄 — 점수 없음 {unscored} · 사람 라벨 없음 {unlabeled} · **재생 {cases.Count}** (정상 {cases.Count(c => !c.Defect)} · 불량 {cases.Count(c => c.Defect)})");
        sb.AppendLine("- 점수는 클수록 정상 기억에서 멀다. 비교 지표는 문턱에 기대지 않는다: AUROC(불량 점수가 정상 점수보다 높을 확률)와 «정상 오경보 N % 를 허용할 때 점수가 그 문턱 이하라 통과한 불량 수».");
        sb.AppendLine("- 기억 크기 = 판정 당시 정상 기억의 사진 수. 표본이 작은 칸(정상 또는 불량 30 미만)은 우열을 가를 수 없다 — «표본 적음».");
        sb.AppendLine();
        sb.AppendLine("| 기억 크기 | 정상 | 불량 | AUROC 전역 → 패치 | 오경보 1 % 놓친 불량 | 5 % | 10 % | 읽기 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var (label, subset) in Groups(cases))
        {
            var normal = subset.Where(c => !c.Defect).ToList();
            var defect = subset.Where(c => c.Defect).ToList();
            if (normal.Count == 0 || defect.Count == 0)
            {
                sb.AppendLine($"| {label} | {normal.Count} | {defect.Count} | — | — | — | — | 한쪽 클래스가 없음 |");
                continue;
            }
            double[] gn = [.. normal.Select(c => c.Global)], gd = [.. defect.Select(c => c.Global)];
            double[] pn = [.. normal.Select(c => c.Patch)], pd = [.. defect.Select(c => c.Patch)];
            var missed = FalseAlarmRates.Select(r =>
                $"{PatchScoring.MissedAtFalseAlarm(gn, gd, r)} → {PatchScoring.MissedAtFalseAlarm(pn, pd, r)} /{defect.Count}").ToList();
            var small = normal.Count < 30 || defect.Count < 30 ? "표본 적음" : "";
            sb.AppendLine(string.Create(inv,
                $"| {label} | {normal.Count} | {defect.Count} | {PatchScoring.Auroc(gn, gd):F3} → {PatchScoring.Auroc(pn, pd):F3} | {missed[0]} | {missed[1]} | {missed[2]} | {small} |"));
        }
        return sb.ToString();
    }

    private static IEnumerable<(string Label, List<ScoredCase> Cases)> Groups(IReadOnlyList<ScoredCase> cases)
    {
        for (var i = 0; i < BankEdges.Length; i++)
        {
            var lo = BankEdges[i];
            var hi = i + 1 < BankEdges.Length ? BankEdges[i + 1] : int.MaxValue;
            var subset = cases.Where(c => c.BankSize >= lo && c.BankSize < hi).ToList();
            if (subset.Count == 0) continue;
            yield return (hi == int.MaxValue ? $"{lo}장 이상" : $"{lo}~{hi - 1}장", subset);
        }
        // 크기가 섞인 행 — 패치 점수는 기억이 작을수록 크게 나와(비교할 정상 패치가 적어서) 섞으면 두 점수의 우열이 왜곡된다. 참고만.
        yield return ("전체(크기 섞임 — 참고만)", [.. cases]);
    }
}
