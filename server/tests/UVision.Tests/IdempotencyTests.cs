using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UVision.Api;
using UVision.Api.Configuration;
using UVision.Api.Storage;
using UVision.Api.Services.Inspection;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 검사 요청의 멱등 키(<c>Idempotency-Key</c>) — 응답을 잃은 장치가 같은 캡처를 다시 보내도 판정 · 레코드 · 결정 장부 ·
/// 판정 사건이 한 번만 생기고, 두 번째 응답은 첫 응답과 byte 가 같다.
/// </summary>
public class IdempotencyTests
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static MultipartFormDataContent Form(byte[]? image = null, string? capturedAt = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image ?? Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        form.Add(new StringContent("tab-1"), "device_id");
        if (capturedAt is not null) form.Add(new StringContent(capturedAt), "captured_at");
        return form;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? key, MultipartFormDataContent form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        if (key is not null) request.Headers.Add(InspectionReplays.KeyHeader, key);
        return await client.SendAsync(request);
    }

    private static WebApplicationFactory<Program> WithWebhook(UVisionApiFactory factory) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("UVision:Events:Webhooks:0:Name", "mes");
            b.UseSetting("UVision:Events:Webhooks:0:Url", "http://127.0.0.1:9/hook"); // 닿지 않음 — outbox 에 남는다
        });

    private static int Count(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).Length : 0;

    private static int LedgerLines(string dataPath) =>
        Directory.Exists(Path.Combine(dataPath, "demo", "ledger"))
            ? Directory.GetFiles(Path.Combine(dataPath, "demo", "ledger"), "*.jsonl")
                .Sum(f => File.ReadAllLines(f).Count(l => l.Trim().Length > 0))
            : 0;

    private static int ResultRecords(string dataPath) =>
        Directory.EnumerateDirectories(Path.Combine(dataPath, "demo"))
            .Where(d => DateOnly.TryParseExact(Path.GetFileName(d), "yyyy-MM-dd", out _))
            .Sum(d => Directory.GetFiles(d, "img_*.json").Count(f => !f.EndsWith(".label.json")));

    [Theory]
    [InlineData("/api/u-vision/inspect")]
    [InlineData("/api/u-vision/v2/inspect")]
    public async Task Same_Key_Replays_The_First_Response_And_Records_Once(string path)
    {
        using var factory = new UVisionApiFactory();
        using var app = WithWebhook(factory);
        var client = app.CreateClient();
        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(client, path, key, Form(capturedAt: "2026-10-22T09:00:00+09:00"));
        var second = await PostAsync(client, path, key, Form(capturedAt: "2026-10-22T09:00:00+09:00"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(first.Headers.Contains(InspectionReplays.ReplayedHeader));
        Assert.Equal("true", Assert.Single(second.Headers.GetValues(InspectionReplays.ReplayedHeader)));
        Assert.Equal("application/json; charset=utf-8", second.Content.Headers.ContentType!.ToString());
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await second.Content.ReadAsByteArrayAsync());

        Assert.Equal(1, ResultRecords(factory.DataPath));
        Assert.Equal(1, LedgerLines(factory.DataPath));
        Assert.Equal(1, Count(Path.Combine(factory.DataPath, "demo", "events", "mes"), "*.json"));
    }

    [Fact]
    public async Task Keyed_Response_Is_The_Same_Shape_As_Unkeyed()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var keyed = await PostAsync(client, "/api/u-vision/inspect", "k1", Form());
        var plain = await PostAsync(client, "/api/u-vision/inspect", null, Form());

        using var a = JsonDocument.Parse(await keyed.Content.ReadAsStringAsync());
        using var b = JsonDocument.Parse(await plain.Content.ReadAsStringAsync());
        Assert.Equal(
            a.RootElement.EnumerateObject().Select(p => p.Name),
            b.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(plain.Content.Headers.ContentType!.ToString(), keyed.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Different_Keys_Are_Different_Inspections()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        await PostAsync(client, "/api/u-vision/inspect", "a", Form());
        await PostAsync(client, "/api/u-vision/inspect", "b", Form());
        await PostAsync(client, "/api/u-vision/inspect", null, Form());
        await PostAsync(client, "/api/u-vision/inspect", null, Form());

        Assert.Equal(4, ResultRecords(factory.DataPath));
    }

    [Fact]
    public async Task Same_Key_With_A_Different_Request_Is_Rejected()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        await PostAsync(client, "/api/u-vision/inspect", "k", Form());
        var otherImage = await PostAsync(client, "/api/u-vision/inspect", "k", Form(image: [.. Jpeg, 0x02]));
        var otherEndpoint = await PostAsync(client, "/api/u-vision/v2/inspect", "k", Form());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, otherImage.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, otherEndpoint.StatusCode);
        Assert.Equal(1, ResultRecords(factory.DataPath));
    }

    // 빈 헤더는 바인딩에서 «키 없음»이 된다 — 키 없는 요청과 같이 판정한다.
    [Theory]
    [InlineData("../x")]
    [InlineData("a b")]
    public async Task Malformed_Key_Is_400(string key)
    {
        using var factory = new UVisionApiFactory();
        var response = await PostAsync(factory.CreateClient(), "/api/u-vision/inspect", key, Form());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, ResultRecords(factory.DataPath));
    }

    [Fact]
    public async Task Concurrent_Requests_With_One_Key_Inspect_Once()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => PostAsync(client, "/api/u-vision/inspect", "same", Form())));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Equal(1, ResultRecords(factory.DataPath));
    }

    [Theory]
    [InlineData(168, 1)]
    [InlineData(0, 0)] // 0 이하 = 지우지 않음
    public async Task Expired_Replays_Are_Pruned_And_The_Key_Becomes_New(int retentionHours, int expectedRemoved)
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        await PostAsync(client, "/api/u-vision/inspect", "old", Form());
        await PostAsync(client, "/api/u-vision/inspect", "fresh", Form());

        var storage = new StorageOptions { DataPath = factory.DataPath, IdempotencyRetentionHours = retentionHours };
        var paths = new StoragePaths(storage, AppContext.BaseDirectory);
        File.SetLastWriteTimeUtc(paths.ReplayFile("demo", "old"), DateTime.UtcNow.AddHours(-200));
        var replays = new InspectionReplays(
            paths, storage, Options.Create(new JsonOptions()), NullLogger<InspectionReplays>.Instance);

        Assert.Equal(expectedRemoved, replays.PruneExpired(DateTime.UtcNow));
        Assert.True(File.Exists(paths.ReplayFile("demo", "fresh")));
        Assert.Equal(expectedRemoved == 0, File.Exists(paths.ReplayFile("demo", "old")));

        // 지워진 키는 다시 판정된다(재전송 응답이 아니다).
        var again = await PostAsync(client, "/api/u-vision/inspect", "old", Form());
        Assert.Equal(expectedRemoved == 0, again.Headers.Contains(InspectionReplays.ReplayedHeader));
    }
}
