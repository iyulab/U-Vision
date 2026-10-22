using System.Globalization;

namespace UVision.Api.Models;

/// <summary>
/// 요청의 <c>captured_at</c>(장치 캡처 시각) 검증 · 정규화. 시간대가 있는 ISO 8601 만 받고(장치 현지 시각을
/// 서버가 추측하지 않는다) UTC 왕복 형식으로 저장한다 — <c>timestamp</c> 와 같은 모양.
/// </summary>
public static class CaptureTime
{
    private const int MaxLength = 40;

    /// <summary>비었으면 통과(값 없음). 있으면 파싱돼야 통과하고 <paramref name="normalized"/> 에 UTC 값.</summary>
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        var text = value.Trim();
        if (text.Length > MaxLength || !HasOffset(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        normalized = parsed.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        return true;
    }

    // "2026-10-22T10:00:00" 처럼 시간대가 빠진 값은 장치 현지인지 UTC 인지 알 수 없다 — 거부한다.
    private static bool HasOffset(string text)
    {
        var t = text.IndexOf('T');
        if (t < 0)
            return false;
        var time = text[(t + 1)..];
        return time.EndsWith('Z') || time.EndsWith('z') || time.Contains('+') || time.Contains('-');
    }
}
