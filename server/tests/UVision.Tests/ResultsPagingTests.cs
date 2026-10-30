using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 결과 목록 페이지(<c>limit</c> · <c>cursor</c>) — 레코드 하나가 파일 열기 한 번이라 느린 저장소에서 «그날 전부» 를 읽는 대신
/// 쪽으로 읽게 한다. limit 를 안 주면 지금과 같고, 주면 최신 먼저 한 쪽이며, 쪽 사이에 새 검사가 들어와도 밀리거나 겹치지 않는다.
/// 커서는 서버가 낸 값 그대로만 받는다.
/// </summary>
public class ResultsPagingTests
{
    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static byte[] Jpeg(byte tail) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, tail];

    private static async Task<string> InspectAsync(HttpClient client, byte tail)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg(tail));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        var resp = await client.PostAsync("/api/u-vision/inspect", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("image_id").GetString()!;
    }

    /// <summary>레코드 n 건을 만들고 기록 시각을 정해 준다(첫 건이 가장 오래됨) — 순서를 결정적으로.</summary>
    private static async Task<List<string>> SeedAsync(UVisionApiFactory factory, HttpClient client, int count, Func<int, DateTime>? stamp = null)
    {
        var ids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var id = await InspectAsync(client, (byte)(i + 1));
            ids.Add(id);
            File.SetLastWriteTimeUtc(
                Path.Combine(factory.DataPath, "demo", Today, id + ".json"),
                stamp?.Invoke(i) ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }
        return ids;
    }

    private static async Task<(List<string> Ids, string? Next)> PageAsync(HttpClient client, int limit, string? cursor = null)
    {
        var url = $"/api/u-vision/results?scenario_id=demo&date={Today}&limit={limit}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var ids = (await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(r => r.GetProperty("image_id").GetString()!).ToList();
        return (ids, resp.Headers.TryGetValues("X-Next-Cursor", out var v) ? v.Single() : null);
    }

    [Fact]
    public async Task WithoutLimit_ReturnsEverythingAsBefore_WithNoCursorHeader()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        var ids = await SeedAsync(factory, client, 5);

        var resp = await client.GetAsync($"/api/u-vision/results?scenario_id=demo&date={Today}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("X-Next-Cursor"));
        var all = (await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Select(r => r.GetProperty("image_id").GetString()).ToList();
        Assert.Equal(ids.Order(StringComparer.Ordinal), all.Order(StringComparer.Ordinal));   // 같은 건수 · 같은 레코드(순서는 이전 규칙 그대로)
    }

    [Fact]
    public async Task Pages_AreNewestFirst_CoverEveryRecordOnce_AndTheLastPageHasNoCursor()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        var ids = await SeedAsync(factory, client, 5);   // ids[4] 가 가장 최신

        var (first, c1) = await PageAsync(client, 2);
        var (second, c2) = await PageAsync(client, 2, c1);
        var (third, c3) = await PageAsync(client, 2, c2);

        Assert.Equal([ids[4], ids[3]], first);
        Assert.Equal([ids[2], ids[1]], second);
        Assert.Equal([ids[0]], third);
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.Null(c3);
    }

    [Fact]
    public async Task APageThatEndsExactlyAtTheEnd_HasNoCursor()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        await SeedAsync(factory, client, 2);

        var (items, next) = await PageAsync(client, 2);

        Assert.Equal(2, items.Count);
        Assert.Null(next);
    }

    [Fact]
    public async Task NewInspectionsBetweenPages_DoNotShiftOrRepeatTheRest()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        var ids = await SeedAsync(factory, client, 4);

        var (first, cursor) = await PageAsync(client, 2);
        // 쪽을 넘기는 사이 새 검사가 하나 들어온다 — 가장 최신 시각.
        var fresh = await InspectAsync(client, 99);
        File.SetLastWriteTimeUtc(Path.Combine(factory.DataPath, "demo", Today, fresh + ".json"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var (second, _) = await PageAsync(client, 2, cursor);
        var (reload, _) = await PageAsync(client, 2);

        Assert.Equal([ids[3], ids[2]], first);
        Assert.Equal([ids[1], ids[0]], second);          // 밀리지 않고 겹치지 않는다
        Assert.Equal([fresh, ids[3]], reload);           // 다시 처음부터 부르면 새 검사가 맨 위
    }

    [Fact]
    public async Task RecordsWithTheSameStamp_AreStillPagedWithoutLossOrRepeat()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        var same = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        var ids = await SeedAsync(factory, client, 4, _ => same);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var (items, next) = await PageAsync(client, 1, cursor);
            seen.AddRange(items);
            cursor = next;
        } while (cursor is not null);

        Assert.Equal(ids.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task NoRecordsForTheDate_IsAnEmptyPageWithoutCursor()
    {
        using var factory = new UVisionApiFactory();
        var (items, next) = await PageAsync(factory.CreateClient(), 5);
        Assert.Empty(items);
        Assert.Null(next);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=-3")]
    [InlineData("limit=1001")]
    [InlineData("limit=abc")]
    [InlineData("cursor=1.img_x")]              // limit 없이 cursor
    [InlineData("limit=2&cursor=garbage")]
    [InlineData("limit=2&cursor=12.")]
    [InlineData("limit=2&cursor=.img_x")]
    [InlineData("limit=2&cursor=12.../x")]
    [InlineData("limit=2&cursor=-5.img_x")]
    public async Task BadPagingArguments_AreRejected(string query)
    {
        using var factory = new UVisionApiFactory();
        var resp = await factory.CreateClient().GetAsync($"/api/u-vision/results?scenario_id=demo&date={Today}&{query}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
