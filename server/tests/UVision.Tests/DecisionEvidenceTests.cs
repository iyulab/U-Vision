using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 판정 근거 조회 — 저장된 판정 한 건을 «누가 · 어떤 경로로 · 무엇을 근거로» 낸 것인지 레코드 + 결정 장부에서 읽어
/// 돌려주는지. 읽기 전용이고, 장부가 없거나 일부만 남은 옛 기록은 있는 만큼만 정직하게 돌려준다.
/// </summary>
public class DecisionEvidenceTests
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static MultipartFormDataContent Form(string scenarioId = "demo")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(scenarioId), "scenario_id");
        return form;
    }

    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<string> InspectAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/api/u-vision/v2/inspect", Form());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("decision_id").GetString()!;
    }

    private static Task<HttpResponseMessage> GetEvidenceAsync(HttpClient client, string imageId, string scenario = "demo", string? date = null) =>
        client.GetAsync($"/api/u-vision/results/decision?scenario_id={scenario}&date={date ?? Today()}&image_id={imageId}");

    [Fact]
    public async Task VlmOnly_ReturnsPathSettledBy_AndOperativeAnnotations()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        var id = await InspectAsync(client);

        var resp = await GetEvidenceAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, body.GetProperty("image_id").GetString());
        Assert.Equal("demo", body.GetProperty("scenario_id").GetString());
        Assert.Contains(body.GetProperty("class").GetString(), new[] { "OK", "NG" });
        Assert.True(body.GetProperty("ledger_recorded").GetBoolean());
        Assert.Equal("vlm", body.GetProperty("settled_by").GetProperty("tier").GetString());
        Assert.Equal("vlm", body.GetProperty("settled_by").GetProperty("kind").GetString());
        var step = Assert.Single(body.GetProperty("path").EnumerateArray());
        Assert.Equal("vlm", step.GetProperty("tier").GetString());
        Assert.Equal("vlm", step.GetProperty("kind").GetString());
        Assert.Equal("settled", step.GetProperty("outcome").GetString());
        // 실제로 쓴 결정이 근거와 나란히 — 사람이 «이 판정이 어디서 나왔나» 를 한 응답에서 읽는다.
        Assert.Equal("vlm", body.GetProperty("annotations").GetProperty("operative:source").GetString());
        // 판정원이 하나면 검토 신호 · ML 가 없다.
        Assert.False(body.TryGetProperty("ml", out _));
    }

    [Fact]
    public async Task MlEnabled_ShowsHeldBelowStep_AndMlAnswer()
    {
        using var factory = new MlEnabledFactory();
        var client = factory.CreateClient();
        var id = await InspectAsync(client);

        var body = await (await GetEvidenceAsync(client, id)).Content.ReadFromJsonAsync<JsonElement>();

        var path = body.GetProperty("path").EnumerateArray().ToList();
        Assert.Equal(2, path.Count);
        Assert.Equal("ml", path[0].GetProperty("tier").GetString());
        Assert.Contains(path[0].GetProperty("outcome").GetString(), new[] { "shadowed", "escalated" });
        Assert.Equal("vlm", path[1].GetProperty("tier").GetString());
        Assert.Equal("settled", path[1].GetProperty("outcome").GetString());
        // 아래 단이 무엇이라 답했는지는 레코드의 ML 결과가 정본이다 — 경로와 한 응답에 합쳐 준다.
        Assert.True(body.GetProperty("ml").TryGetProperty("label", out _));
        Assert.True(body.TryGetProperty("agreement", out _));
    }

    [Fact]
    public async Task LedgerMissing_StillReturnsRecord_WithoutInventingPath()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        var id = await InspectAsync(client);
        File.Delete(Path.Combine(factory.DataPath, "demo", "ledger", Today() + ".jsonl"));

        var resp = await GetEvidenceAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("ledger_recorded").GetBoolean());
        Assert.Contains(body.GetProperty("class").GetString(), new[] { "OK", "NG" });
        // 장부가 없으면 경로 · 확정자를 지어내지 않는다(정직성) — 필드 자체를 생략.
        Assert.False(body.TryGetProperty("path", out _));
        Assert.False(body.TryGetProperty("settled_by", out _));
    }

    [Fact]
    public async Task UnknownImage_Is404_AndBadIds_Are400()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        await InspectAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await GetEvidenceAsync(client, "img_doesnotexist")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetEvidenceAsync(client, "..%2Fx")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetEvidenceAsync(client, "img_a", date: "not-a-date")).StatusCode);
    }
}
