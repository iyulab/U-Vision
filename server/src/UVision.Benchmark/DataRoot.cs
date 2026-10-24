namespace UVision.Benchmark;

/// <summary>
/// 측정 데이터 루트 — <c>--data-root</c> 또는 환경변수 <c>UVISION_DATA_ROOT</c>. 원본은 루트에서 직접 읽고, U-Vision 이 만드는
/// 파생물(표본 목록 · 벡터 캐시)은 루트의 <c>GEN/u-vision/</c> 에만 쓴다.
/// </summary>
internal static class DataRoot
{
    public static string? Resolve(Dictionary<string, string> opts)
    {
        var root = opts.GetValueOrDefault("data-root") ?? Environment.GetEnvironmentVariable("UVISION_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Console.Error.WriteLine("데이터 루트를 찾지 못함: --data-root 또는 UVISION_DATA_ROOT");
            return null;
        }
        return Path.GetFullPath(root);
    }
}
