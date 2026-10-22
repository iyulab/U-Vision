using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 결과 v2 — <c>/v2/inspect</c> 가 v1 과 같은 판정을 클래스 · 확정자 · 라우터 경로로 돌려주는지, 판정 불가를
/// 같은 모양(사유 · 경로)으로 알리는지, 레코드 · 장부가 v1 과 같이 남는지.
/// </summary>
public class InspectV2Tests
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

    [Fact]
    public async Task VlmOnly_ReturnsClassSettledByVlm_AndOneStepPath()
    {
        using var factory = new UVisionApiFactory();
        var resp = await factory.CreateClient().PostAsync("/api/u-vision/v2/inspect", Form());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(body.GetProperty("class").GetString(), new[] { "OK", "NG" });
        Assert.Equal("vlm", body.GetProperty("settled_by").GetProperty("tier").GetString());
        Assert.Equal("vlm", body.GetProperty("settled_by").GetProperty("kind").GetString());
        Assert.Equal("demo", body.GetProperty("scenario_id").GetString());
        var step = Assert.Single(body.GetProperty("path").EnumerateArray());
        Assert.Equal("vlm", step.GetProperty("tier").GetString());
        Assert.Equal("settled", step.GetProperty("outcome").GetString());
        // 판정원이 하나면 검토 신호 · 판정 불가 표시가 없다.
        Assert.False(body.TryGetProperty("requires_review", out _));
        Assert.False(body.TryGetProperty("detection_unavailable", out _));
    }

    [Fact]
    public async Task CaptureOrigin_IsEchoed_WhenSent_AndOmittedOtherwise()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var plain = await (await client.PostAsync("/api/u-vision/v2/inspect", Form()))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(plain.TryGetProperty("device_id", out _));
        Assert.False(plain.TryGetProperty("captured_at", out _));

        var form = Form();
        form.Add(new StringContent("tab-9"), "device_id");
        form.Add(new StringContent("2026-10-22T09:00:00+09:00"), "captured_at");
        var body = await (await client.PostAsync("/api/u-vision/v2/inspect", form))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tab-9", body.GetProperty("device_id").GetString());
        Assert.False(body.TryGetProperty("device_label", out _));
        Assert.Equal("2026-10-22T00:00:00.0000000Z", body.GetProperty("captured_at").GetString());
    }

    [Fact]
    public async Task Completed_IsRecordedLikeV1_UnderTheDecisionId()
    {
        using var factory = new UVisionApiFactory();
        var body = await (await factory.CreateClient().PostAsync("/api/u-vision/v2/inspect", Form()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("decision_id").GetString()!;

        var records = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/u-vision/results?scenario_id=demo");
        var record = Assert.Single(records.EnumerateArray());
        Assert.Equal(id, record.GetProperty("image_id").GetString());
        Assert.Equal(body.GetProperty("class").GetString(), record.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task MlEnabled_PathShowsMlBelowVlm_AndReviewSignal()
    {
        using var factory = new MlEnabledFactory();
        var resp = await factory.CreateClient().PostAsync("/api/u-vision/v2/inspect", Form());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var path = body.GetProperty("path").EnumerateArray().ToList();
        Assert.Equal(["ml", "vlm"], path.Select(s => s.GetProperty("tier").GetString()));
        Assert.Contains(path[0].GetProperty("outcome").GetString(), new[] { "shadowed", "escalated" });
        Assert.Equal("settled", path[1].GetProperty("outcome").GetString());
        // 판정원이 둘이면 검토 신호가 함께 온다(값은 일치 · 신뢰도에 따라).
        Assert.Contains(body.GetProperty("requires_review").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
    }

    [Fact]
    public async Task VlmDown_Returns503_WithReasonAndPath_NoClass()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IVlmProvider>(new ThrowingVlmProvider()))).CreateClient();
        var resp = await client.PostAsync("/api/u-vision/v2/inspect", Form());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("detection_unavailable").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("reason").GetString()));
        Assert.StartsWith("dec_", body.GetProperty("decision_id").GetString());
        Assert.Equal("abstained", body.GetProperty("path").EnumerateArray().Last().GetProperty("outcome").GetString());
        Assert.False(body.TryGetProperty("class", out _));
        Assert.False(body.TryGetProperty("settled_by", out _));
    }

    [Theory]
    [InlineData("nope", HttpStatusCode.NotFound)]
    [InlineData("../x", HttpStatusCode.BadRequest)]
    public async Task UnknownOrInvalidScenario_IsRejectedLikeV1(string scenarioId, HttpStatusCode expected)
    {
        using var factory = new UVisionApiFactory();
        var resp = await factory.CreateClient().PostAsync("/api/u-vision/v2/inspect", Form(scenarioId));
        Assert.Equal(expected, resp.StatusCode);
    }

    private sealed class ThrowingVlmProvider : IVlmProvider
    {
        public string Name => "throwing-vlm";
        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated VLM endpoint down");
    }
}
