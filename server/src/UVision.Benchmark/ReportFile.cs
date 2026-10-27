using System.Text;

namespace UVision.Benchmark;

/// <summary>측정 리포트를 파일로 남긴다 — 폴더가 없으면 만들고, 글자는 BOM 없는 UTF-8(한글 리포트가 깨지지 않게).</summary>
internal static class ReportFile
{
    internal static async Task WriteAsync(string path, string report)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, report, new UTF8Encoding(false));
        Console.Error.WriteLine($"→ 리포트 저장: {full}");
    }
}
