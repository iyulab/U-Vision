using System.Globalization;
using System.Text;
using HoneAI;
using HoneAI.Routing;
using HoneAI.Trust;

namespace UVision.Benchmark;

/// <summary>
/// 반복 일관성 — 같은 판정원이 같은 사진을 두 번(둘 다 정답을 가린 채) 판정한 두 VLM 리포트를 짝지어, 답이 움직이는 몫(잡음)과
/// 같은 오답을 되풀이하는 몫(체계적 오류)을 가른다(HoneAI <see cref="RepeatConsistency"/>). 잡음은 다시 물으면 줄고
/// 체계적 오류는 줄지 않는다 — «다시 물으면 올릴지» 와 교사 자격(정답 대비 정확도만으로는 못 보는 것)의 근거.
/// <para>
/// 두 리포트는 같은 표본 · 같은 설정이어야 한다(리포트 머리의 모드 · 모델이 같아야 짝이 된다). 판정 불가(실패)는 답이 아니라
/// 기권으로 센다 — 반복이 «둘 다 답한 건»에서만 재진다.
/// </para>
/// </summary>
internal static class RepeatBench
{
    private static readonly ISettlementPolicy<string> Policy = SettlementPolicy.Categorical(
        settleable: ["OK"], mustCatch: ["NG"], comparer: StringComparer.OrdinalIgnoreCase);

    private sealed record Row(string Image, string Truth, string? Verdict, double Confidence);

    public static int Run(Dictionary<string, string> o)
    {
        if (!o.TryGetValue("a", out var a) || !o.TryGetValue("b", out var b))
        {
            Console.Error.WriteLine("repeat: --a <리포트1.md> --b <리포트2.md> 필수 — 선택 --out <리포트.md>");
            return 1;
        }
        var first = Parse(a);
        var second = Parse(b).ToDictionary(r => r.Image, StringComparer.Ordinal);
        var pairs = first.Where(r => second.ContainsKey(r.Image)).Select(r => (A: r, B: second[r.Image])).ToList();
        if (pairs.Count == 0)
        {
            Console.Error.WriteLine("repeat: 두 리포트에 같은 이미지가 없다");
            return 1;
        }

        var tier = new Tier { Id = "vlm", Kind = JudgeKind.Vlm, Version = "report", CostRank = 2 };
        var identity = new JudgeIdentity("vlm");
        var now = DateTimeOffset.UtcNow;
        RecheckResult<string> Result(Row r, string id) => new(id, tier,
            r.Verdict is { } v ? JudgeVerdict<string>.Answer(v, r.Confidence) : JudgeVerdict<string>.Abstain("failed"), now);

        var records = pairs.Select(p => RepeatConsistency.Pair(
            Result(p.A, p.A.Image), Result(p.B, p.A.Image), "report", Policy, new Known<string>(p.A.Truth))).ToList();
        var cell = RepeatConsistency.Compute(records, identity);

        var report = Render(a, b, pairs.Count, records, cell);
        Console.WriteLine(report);
        if (o.TryGetValue("out", out var outPath))
            File.WriteAllText(outPath, report, new UTF8Encoding(true));
        return 0;
    }

    private static string Pct(double? v) => v is { } x ? (x * 100).ToString("F1", CultureInfo.InvariantCulture) + " %" : "—";

    private static string Render(string a, string b, int pairs, List<RepeatRecord> records, RepeatCell cell)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 반복 일관성 리포트");
        sb.AppendLine();
        sb.AppendLine($"- 첫 번째: `{Path.GetFileName(a)}` · 두 번째: `{Path.GetFileName(b)}`");
        sb.AppendLine($"- 짝 지은 이미지 {pairs} — 둘 다 답한 반복 **{cell.Repeats}** (한쪽이라도 판정 불가면 제외 {pairs - cell.Repeats})");
        sb.AppendLine();
        sb.AppendLine("| 지표 | 값 | 뜻 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| 일관성 | {Pct(cell.Consistency)} | 두 답이 같은 몫 |");
        sb.AppendLine($"| 잡음(NoiseRate) | {Pct(cell.NoiseRate)} | 두 답이 다른 몫 — 다시 물으면 움직인다 |");
        sb.AppendLine($"| 체계적 오류(SystematicErrorRate) | {Pct(cell.SystematicErrorRate)} | 두 번 다 같은 오답 — 다시 물어도 안 움직인다 |");
        sb.AppendLine($"| 일치했을 때 정확도 | {Pct(cell.AccuracyWhenConsistent)} | 두 답이 같은 건 중 둘 다 맞은 몫 |");
        sb.AppendLine($"| 불량 판정이 뒤집힌 수 | {cell.MustCatchFlips} | 한쪽은 NG, 한쪽은 OK 로 부른 건 |");
        sb.AppendLine();
        var wrongBoth = records.Count(r => r is { Consistent: true, FirstCorrect: false, SecondCorrect: false });
        var flipped = records.Count(r => r.Consistent == false);
        sb.AppendLine($"한 줄: 답이 갈린 건 {flipped}, 같은 오답을 두 번 한 건 {wrongBoth} (알려진 정답이 있는 반복 {cell.KnownRepeats} 중).");
        return sb.ToString();
    }

    /// <summary>VLM 리포트의 «건별 결과» 표 — 이미지 · 정답 · 판정 · confidence. 판정이 OK/NG 가 아니면(실패) 답 없음.</summary>
    private static List<Row> Parse(string path)
    {
        var rows = new List<Row>();
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal) || line.StartsWith("| 이미지", StringComparison.Ordinal))
                continue;
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            // 앞뒤 빈 칸(선행 · 후행 '|') 때문에 인덱스가 1부터다.
            if (cells.Length < 5) continue;
            var truth = cells[2];
            if (truth is not ("OK" or "NG")) continue;
            var verdict = cells[3] is "OK" or "NG" ? cells[3] : null;
            _ = double.TryParse(cells[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf);
            rows.Add(new Row(cells[1], truth, verdict, conf));
        }
        return rows;
    }
}
