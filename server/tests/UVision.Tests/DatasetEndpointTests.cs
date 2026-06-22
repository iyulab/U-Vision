using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// FW-1 데이터셋 export 엔드포인트 — export 생성=PIN, 조회=무인증.
/// </summary>
public class DatasetEndpointTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;
    public DatasetEndpointTests(PinFactory factory) => _factory = factory;

    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static string Today =>
        DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static MultipartFormDataContent InspectForm()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        return form;
    }

    /// <summary>inspect → image_id, 그 결과에 사람 라벨을 단다(export 대상 생성).</summary>
    private static async Task<string> InspectAndLabel(HttpClient client, string label)
    {
        var resp = await client.PostAsync("/api/u-vision/inspect", InspectForm());
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = json.GetProperty("image_id").GetString()!;
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = label });
        return imageId;
    }

    private HttpClient PinClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        return client;
    }

    [Fact]
    public async Task Export_WithPin_CreatesManifest_AndListReflects()
    {
        var client = PinClient();
        var ok = await InspectAndLabel(client, "OK");
        await InspectAndLabel(client, "NG");

        var post = await client.PostAsync("/api/u-vision/datasets/export?scenario_id=demo", null);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var manifest = await post.Content.ReadFromJsonAsync<JsonElement>();
        var exportId = manifest.GetProperty("export_id").GetString()!;
        Assert.True(manifest.GetProperty("total").GetInt32() >= 2);
        Assert.Contains(manifest.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("image_id").GetString() == ok);

        // 목록에 반영.
        var list = await (await client.GetAsync("/api/u-vision/datasets?scenario_id=demo"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.EnumerateArray(), e => e.GetString() == exportId);

        // manifest 단건 조회(무인증).
        var unauth = _factory.CreateClient();
        var fetched = await unauth.GetAsync(
            $"/api/u-vision/datasets/{exportId}/manifest?scenario_id=demo");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var fetchedManifest = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(exportId, fetchedManifest.GetProperty("export_id").GetString());
    }

    [Fact]
    public async Task Export_WithoutHeader_401()
    {
        var client = _factory.CreateClient(); // PIN 설정됐으나 헤더 없음
        var post = await client.PostAsync("/api/u-vision/datasets/export?scenario_id=demo", null);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }

    [Fact]
    public async Task Export_UnknownScenario_404()
    {
        var client = PinClient();
        var post = await client.PostAsync("/api/u-vision/datasets/export?scenario_id=nope", null);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task List_NoAuth_EmptyForFreshScenario()
    {
        // PIN 미설정 팩토리에서도 조회(무인증)는 동작.
        using var plain = new UVisionApiFactory();
        var client = plain.CreateClient();
        var resp = await client.GetAsync("/api/u-vision/datasets?scenario_id=demo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(list.EnumerateArray());
    }

    [Fact]
    public async Task Manifest_Missing_404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/u-vision/datasets/nonexistent/manifest?scenario_id=demo");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
