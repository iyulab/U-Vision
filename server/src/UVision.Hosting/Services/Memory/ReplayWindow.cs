using System.Globalization;

namespace UVision.Api.Services.Memory;

/// <summary>문턱 재생이 읽는 날짜 창 — 순수 함수. 마지막 날(<c>date</c>)부터 거슬러 올라간다.</summary>
public static class ReplayWindow
{
    /// <summary>한 번에 읽는 날의 상한 — 장부를 날마다 읽는 비용(네트워크 공유에서는 날마다 파일 열기)을 묶는다.</summary>
    public const int MaxDays = 90;

    /// <summary>마지막 날부터 <paramref name="days"/> 일, 오래된 날부터 순서대로. 날짜 형식이 아니면 <see cref="ArgumentException"/>.</summary>
    public static IReadOnlyList<string> Dates(string lastDate, int days)
    {
        if (!DateOnly.TryParseExact(lastDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
            throw new ArgumentException("날짜 형식은 yyyy-MM-dd 여야 합니다", nameof(lastDate));
        return [.. Enumerable.Range(0, days).Select(i => last.AddDays(i - (days - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
    }
}
