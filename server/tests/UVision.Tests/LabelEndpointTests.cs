using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 라벨 엔드포인트 — 무인증 운영 데이터 쓰기, 사이드카 분리, 불변 VLM 레코드 보존.
/// </summary>
public class LabelEndpointTests : IClassFixture<UVisionApiFactory>
{
    private readonly UVisionApiFactory _factory;
    public LabelEndpointTests(UVisionApiFactory factory) => _factory = factory;

    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static MultipartFormDataContent InspectForm()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        return form;
    }

    private static string Today =>
        DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>inspect 후 image_id 반환 — 라벨 대상 결과를 만든다.</summary>
    private static async Task<string> InspectAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/api/u-vision/inspect", InspectForm());
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("image_id").GetString()!;
    }

    [Fact]
    public async Task PutLabel_NoAuth_WritesSidecar_AndGetReflects()
    {
        var client = _factory.CreateClient();
        var imageId = await InspectAsync(client);

        // PIN 헤더 없이(무인증) 라벨 쓰기 — 200.
        var put = await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = "NG" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var labels = await (await client.GetAsync($"/api/u-vision/results/labels?scenario_id=demo&date={Today}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var match = labels.EnumerateArray()
            .First(l => l.GetProperty("image_id").GetString() == imageId);
        Assert.Equal("NG", match.GetProperty("label").GetString());
    }

    [Fact]
    public async Task DeleteLabel_RemovesSidecar()
    {
        var client = _factory.CreateClient();
        var imageId = await InspectAsync(client);
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = "OK" });

        var del = await client.DeleteAsync(
            $"/api/u-vision/results/label?scenario_id=demo&date={Today}&image_id={imageId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var labels = await (await client.GetAsync($"/api/u-vision/results/labels?scenario_id=demo&date={Today}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(labels.EnumerateArray(),
            l => l.GetProperty("image_id").GetString() == imageId);
    }

    [Fact]
    public async Task PutLabel_RejectsValueOutsideLabelSet()
    {
        var client = _factory.CreateClient();
        var imageId = await InspectAsync(client);
        var put = await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = "MAYBE" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode); // 집합 밖 → 400
    }

    [Fact]
    public async Task PutLabel_RejectsMalformedImageId()
    {
        var client = _factory.CreateClient();
        var put = await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = "../etc", Label = "OK" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode); // 경로 주입 차단
    }

    [Fact]
    public async Task Labeling_LeavesResultJson_ByteUnchanged()
    {
        // 불변식 가드: 라벨링은 VLM 레코드를 건드리지 않는다.
        var client = _factory.CreateClient();
        var imageId = await InspectAsync(client);

        var before = await ReadResultJson(client, imageId);
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = "NG" });
        var after = await ReadResultJson(client, imageId);

        Assert.Equal(before, after); // 결과 레코드 byte 동일
    }

    [Fact]
    public async Task Labeling_DoesNotLeakInto_ResultsList()
    {
        // 라벨 사이드카가 결과 목록(*.json)에 끌려들어오지 않는다.
        var client = _factory.CreateClient();
        var imageId = await InspectAsync(client);
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = "OK" });

        var results = await (await client.GetAsync($"/api/u-vision/results?scenario_id=demo&date={Today}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        // 모든 결과 항목이 정상 StoredResult(verdict 보유) — 라벨 사이드카가 섞이지 않음.
        Assert.All(results.EnumerateArray(),
            r => Assert.True(r.GetProperty("verdict").GetString() is "OK" or "NG"));
    }

    /// <summary>결과 json 의 raw 응답 텍스트(byte 비교용) — results 목록에서 해당 image_id 항목.</summary>
    private static async Task<string> ReadResultJson(HttpClient client, string imageId)
    {
        var results = await (await client.GetAsync($"/api/u-vision/results?scenario_id=demo&date={Today}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var match = results.EnumerateArray()
            .First(r => r.GetProperty("image_id").GetString() == imageId);
        return match.GetRawText();
    }

    [Fact]
    public async Task PutLabel_NoRecord_Returns404_AndLeavesNothingBehind()
    {
        var client = _factory.CreateClient();

        // 오타 난 image_id · 아직 없는 날짜 · 존재하지 않는(또는 지워진) 시나리오 — 모두 «기록됐다» 로 답하지 않고 사이드카도 남기지 않는다.
        foreach (var (scenario, date, image) in new[] { ("demo", Today, "img_typo"), ("demo", "2020-01-01", "img_typo"), ("ghost", Today, "img_typo") })
        {
            var put = await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
            { ScenarioId = scenario, Date = date, ImageId = image, Label = "OK" });
            Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        }
        var labels = await client.GetFromJsonAsync<JsonElement>($"/api/u-vision/results/labels?scenario_id=demo&date={Today}");
        Assert.DoesNotContain(labels.EnumerateArray(), l => l.GetProperty("image_id").GetString() == "img_typo");   // 같은 픽스처의 다른 시험이 단 라벨은 남아 있다
        Assert.False(Directory.Exists(Path.Combine(_factory.DataPath, "ghost")), "없는 시나리오의 디렉터리를 만들었다");

        // 형식이 틀린 id 는 그대로 400(존재 확인 이전의 경계 거부).
        var bad = await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = "../x", Label = "OK" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

}
