using System.Globalization;
using System.Text;
using HoneAI;
using UVision.Api.Configuration;
using UVision.Api.Services.Memory;
using UVision.Api.Storage;

namespace UVision.Benchmark;

/// <summary>
/// 장부로 이웃 수 k 다시 세기 — 엔진이 쌓은 결정 장부의 <c>memory:neighbor_classes</c> · <c>memory:neighbor_distances</c>(기억 단이 본 가까운
/// 이웃의 순서)와 사람 라벨을 이미지 id 로 이어, 다른 k 였다면 기억 단이 어떻게 답했을지를 <b>같은 기억 · 같은 질의 그대로</b> 다시 센다.
/// 오프라인 <c>memory --neighbors K</c> 스윕(표본 목록을 다시 임베딩해 재생)과 달리 엔진이 실제로 본 판정에서 낸다. 읽기 전용이다.
/// <para>
/// 이웃 기록이 없는 줄(기록 경로가 생기기 전의 줄 · 기억 없음 · 이웃 부족 기권)은 다시 셀 수 없어 센 뒤 리포트에 장수를 밝힌다. 정답은
/// 사람 라벨 — OK 가 정상, 그 밖은 불량 — 이고 라벨 없는 줄은 정답을 몰라 뺀다.
/// </para>
/// </summary>
internal static class LedgerNeighbors
{
    /// <summary>k 하나를 다시 센 결과 — 답함 · 정답 일치 · 불량을 OK 로 답해 통과시킨 수.</summary>
    internal sealed record Row(int K, int Answered, int Agreed, int Missed);

    /// <summary>한 줄의 이웃 기록과 사람 라벨.</summary>
    internal sealed record Case(IReadOnlyDictionary<string, string> Annotations, string Label);

    internal static IReadOnlyList<Row> Recount(IReadOnlyList<Case> cases, IReadOnlyList<int> ks, double maxDistance)
    {
        var rows = new List<Row>();
        foreach (var k in ks)
        {
            int answered = 0, agreed = 0, missed = 0;
            foreach (var c in cases)
            {
                var answer = MemoryJudge.Reconsider(c.Annotations, k, maxDistance);
                if (answer?.Class is not { } cls) continue; // 기권(이웃이 갈림 · 문턱 밖) 또는 이 k 로는 다시 셀 수 없음
                answered++;
                var labelIsOk = string.Equals(c.Label, "OK", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(cls, c.Label, StringComparison.OrdinalIgnoreCase)) agreed++;
                if (!labelIsOk && string.Equals(cls, "OK", StringComparison.OrdinalIgnoreCase)) missed++;
            }
            rows.Add(new Row(k, answered, agreed, missed));
        }
        return rows;
    }

    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        if (!o.TryGetValue("data", out var data) || !o.TryGetValue("scenario", out var scenarioId))
        {
            Console.Error.WriteLine("ledger-neighbors: --data <엔진 데이터 경로> · --scenario <id> 필수 — 선택 --k 1,3,5,9 · --max-distance 0.15 · --out <리포트.md>");
            return 1;
        }
        var ks = (o.GetValueOrDefault("k") ?? "1,3,5,9").Split(',').Select(int.Parse).ToList();
        var maxDistance = double.TryParse(o.GetValueOrDefault("max-distance"), NumberStyles.Float, CultureInfo.InvariantCulture, out var md)
            ? md : new MemoryOptions().MaxDistance;
        var ledgerDir = Path.Combine(data, scenarioId, "ledger");
        if (!Directory.Exists(ledgerDir))
        {
            Console.Error.WriteLine($"ledger-neighbors: 장부 폴더가 없다 — {ledgerDir}");
            return 1;
        }

        var labelStore = new FileLabelStore(new StoragePaths(new StorageOptions { DataPath = data }, AppContext.BaseDirectory));
        var cases = new List<Case>();
        int lines = 0, noRecord = 0, unlabeled = 0;
        foreach (var file in Directory.EnumerateFiles(ledgerDir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            var date = Path.GetFileNameWithoutExtension(file);
            var labels = (await labelStore.ListAsync(scenarioId, date))
                .Where(l => l.OperativeLabel is not null)
                .ToDictionary(l => l.ImageId, l => l.OperativeLabel!, StringComparer.Ordinal);
            await foreach (var record in new JsonlProvenanceSink(file).ReadAsync())
            {
                lines++;
                var a = record.Provenance.Annotations;
                if (a is null || !a.ContainsKey(MemoryAnswer.NeighborClassesKey)) { noRecord++; continue; }
                if (!labels.TryGetValue(record.SubjectId, out var label)) { unlabeled++; continue; }
                cases.Add(new Case(a, label));
            }
        }

        var defects = cases.Count(c => !string.Equals(c.Label, "OK", StringComparison.OrdinalIgnoreCase));
        var sb = new StringBuilder();
        sb.AppendLine($"# 장부로 다시 센 이웃 수 k — `{scenarioId}`").AppendLine();
        sb.AppendLine($"- 장부 {lines}줄 — 이웃 기록 없음 {noRecord} · 라벨 없음 {unlabeled} · **셀 수 있는 줄 {cases.Count}**(불량 {defects} · 정상 {cases.Count - defects}) · 거리 문턱 {maxDistance.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.AppendLine("- 판정은 엔진이 그때 본 이웃 그대로다(같은 기억 · 같은 질의). 응답률 = 답한 줄 / 센 줄, 불량 통과 = 기억이 OK 로 답했는데 사람 라벨이 불량인 줄.").AppendLine();
        sb.AppendLine("| k | 답함 | 응답률 | 정답 일치 | 불량을 OK 로 답해 통과 | 불량의 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in Recount(cases, ks, maxDistance))
        {
            string P(int n, int d) => d == 0 ? "—" : (100.0 * n / d).ToString("0.0", CultureInfo.InvariantCulture) + " %";
            sb.AppendLine($"| {r.K} | {r.Answered} | {P(r.Answered, cases.Count)} | {P(r.Agreed, r.Answered)} | {r.Missed} | {P(r.Missed, defects)} |");
        }
        var report = sb.ToString();
        Console.WriteLine(report);
        if (o.TryGetValue("out", out var outPath)) File.WriteAllText(outPath, report, new UTF8Encoding(true));
        return 0;
    }
}
