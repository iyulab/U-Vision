using System.Text.RegularExpressions;

namespace UVision.Benchmark;

/// <summary>리포트 표 칸에 들어가는 글 — 실패 사유는 전송 예외 메시지를 그대로 담기 쉬워, 주소가 리포트에 남지 않게 지운다.</summary>
internal static partial class ReportText
{
    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    /// <summary>표 칸 하나 — 주소를 지우고, 줄바꿈 · 세로선을 지우고, 길면 자른다.</summary>
    internal static string Cell(string text)
    {
        var flat = Url().Replace(text, "<url>").ReplaceLineEndings(" ").Replace('|', '/');
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }
}
