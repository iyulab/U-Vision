using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UVision.Benchmark;

/// <summary>
/// 리포트 폴더 → 판정원 비교표. VLM 벤치마크 · ML 분류 리포트(이 하네스가 낸 마크다운)를 다시 읽어 한 표로 모은다 —
/// 손으로 옮기면 수치가 틀리므로 표는 리포트에서만 나온다. «불량 잡음»은 판정 불가를 못 잡음으로 센다
/// (판정 실패가 불량 쪽에 몰리면 정확도만 보는 표가 그것을 가린다).
/// </summary>
internal static class ReportSummary
{
    /// <summary>리포트 한 장의 요약. <c>NgTotal</c> 은 판정 불가가 된 불량을 포함한다(VLM 은 건별 표로 알 수 있다).</summary>
    internal sealed record Row(
        string Name, string Judge, int NgTotal, int NgCaught, int OkTotal, int FalseAlarms, int Unjudged);

    private static readonly Regex Mode = new(@"^- 모드: \*\*(?<m>[^*]+)\*\*(?: \((?<d>[^)]*)\))?", RegexOptions.Multiline);
    private static readonly Regex Model = new(@"^- provider: `[^`]*` / model: `(?<m>[^`]*)`", RegexOptions.Multiline);
    private static readonly Regex Failed = new(@"실패 (?<n>\d+)", RegexOptions.Multiline);

    /// <summary>리포트 본문 하나를 읽는다. 이 하네스의 리포트가 아니거나 NG/OK 이진 표가 아니면 null.</summary>
    internal static Row? Parse(string fileName, string text)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        text = text.TrimStart('﻿');
        if (text.StartsWith("# VLM 벤치마크 리포트", StringComparison.Ordinal))
            return ParseVlm(name, text);
        if (text.StartsWith("# ML 분류 정확도", StringComparison.Ordinal))
            return ParseMl(name, text);
        return null;
    }

    private static Row? ParseVlm(string name, string text)
    {
        // 건별 표: | 이미지 | 정답 | 판정 | … — 정답은 NG/OK/-, 판정은 NG/OK/**실패**
        int ngCaught = 0, ngTotal = 0, okTotal = 0, falseAlarms = 0, unjudged = 0;
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            // 앞뒤 빈 칸을 빼면 이미지 · 정답 · 판정 · …
            if (cells.Length < 5) continue;
            var truth = cells[2];
            var verdict = cells[3];
            if (truth is not ("NG" or "OK") || verdict is not ("NG" or "OK" or "**실패**")) continue;
            if (verdict == "**실패**") unjudged++;
            if (truth == "NG")
            {
                ngTotal++;
                if (verdict == "NG") ngCaught++;
            }
            else
            {
                okTotal++;
                if (verdict == "NG") falseAlarms++;
            }
        }
        if (ngTotal + okTotal == 0) return null;
        var mode = Mode.Match(text);
        var model = Model.Match(text).Groups["m"].Value;
        var kind = mode.Success ? mode.Groups["m"].Value : "?";
        var refs = mode.Groups["d"].Success ? mode.Groups["d"].Value : "";
        var judge = $"VLM {kind}" + (refs.Length > 0 ? $" ({refs})" : "") + (model.Length > 0 ? $" · {model}" : "");
        return new Row(name, judge, ngTotal, ngCaught, okTotal, falseAlarms, unjudged);
    }

    private static Row? ParseMl(string name, string text)
    {
        // 혼동표: 머리 | 정답 \ 예측 | NG | OK | 그 아래 | NG | a | b |
        string[]? header = null;
        var counts = new Dictionary<(string Truth, string Predicted), int>();
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;
            var cells = line.Split('|', StringSplitOptions.TrimEntries)[1..^1];
            if (cells.Length > 0 && cells[0].StartsWith("정답", StringComparison.Ordinal))
            {
                header = cells[1..];
                continue;
            }
            if (header is null || cells.Length != header.Length + 1) continue;
            for (var i = 0; i < header.Length; i++)
                if (int.TryParse(cells[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    counts[(cells[0], header[i])] = n;
        }
        if (header is null || header.Any(c => c is not ("NG" or "OK"))) return null;
        int Of(string truth, string pred) => counts.GetValueOrDefault((truth, pred));
        var failed = Failed.Match(text) is { Success: true } f ? int.Parse(f.Groups["n"].Value, CultureInfo.InvariantCulture) : 0;
        var ng = Of("NG", "NG") + Of("NG", "OK");
        var ok = Of("OK", "OK") + Of("OK", "NG");
        return new Row(name, "전용 ML", ng, Of("NG", "NG"), ok, Of("OK", "NG"), failed);
    }

    /// <summary>폴더의 리포트를 이름순으로 읽어 마크다운 표 하나로.</summary>
    internal static string Render(IEnumerable<(string FileName, string Text)> reports)
    {
        var rows = reports.Select(r => Parse(r.FileName, r.Text)).OfType<Row>().OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("| 리포트 | 판정원 | 불량 잡음 | 정상 오경보 | 판정 불가 |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows)
        {
            var caught = r.NgTotal > 0 ? string.Create(inv, $"{r.NgCaught}/{r.NgTotal}") : "—";
            var alarms = r.OkTotal > 0 ? string.Create(inv, $"{r.FalseAlarms}/{r.OkTotal}") : "—";
            sb.AppendLine(string.Create(inv, $"| {r.Name} | {r.Judge} | {caught} | {alarms} | {r.Unjudged} |"));
        }
        return sb.ToString();
    }

    /// <summary><c>summary --dir &lt;리포트 폴더&gt;;…</c> — 폴더(들)의 .md 리포트를 하위까지 읽어 표를 낸다.</summary>
    internal static int Run(IReadOnlyDictionary<string, string> opts)
    {
        if (!opts.TryGetValue("dir", out var dirs) || string.IsNullOrWhiteSpace(dirs))
        {
            Console.Error.WriteLine("--dir <리포트 폴더>[;<폴더>…] 가 필요하다");
            return 1;
        }
        var files = new List<(string, string)>();
        foreach (var dir in dirs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"폴더가 없다: {dir}");
                return 1;
            }
            files.AddRange(Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                .Select(p => (p, File.ReadAllText(p))));
        }
        Console.WriteLine(Render(files));
        return 0;
    }
}
