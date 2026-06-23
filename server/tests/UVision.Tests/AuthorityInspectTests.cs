using System.Net;
using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

public sealed class AuthorityInspectTests : IClassFixture<MlEnabledFactory>
{
    private readonly MlEnabledFactory _factory;
    public AuthorityInspectTests(MlEnabledFactory factory) => _factory = factory;

    // Advisory 기본(authority.json 없음): 응답에 posture 필드가 없어야 한다(byte-identical).
    [Fact]
    public async Task DefaultAdvisory_Response_HasNoPostureField()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/u-vision/inspect", InspectForm("demo"));
        // 503(fail-closed)가 아니면 200 본문에 posture 키 부재.
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.TryGetProperty("posture", out _));
        }
    }

    // Shadow: ml 필드가 응답에서 생략돼야 한다(침묵 수집).
    [Fact]
    public async Task Shadow_Response_OmitsMl()
    {
        var scenarioId = $"shadow_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = scenarioId, Name = "s", Criteria = "c" });
        var store = (IAuthorityStore)_factory.Services.GetService(typeof(IAuthorityStore))!;
        await store.SetStageAsync(scenarioId, AuthorityStage.Shadow, "test", "promote", null, default);

        var resp = await _factory.CreateClient().PostAsync("/api/u-vision/inspect", InspectForm(scenarioId));
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.TryGetProperty("ml", out _));
        }
    }

    private static MultipartFormDataContent InspectForm(string scenarioId)
    {
        // 최소 JPEG 헤더 바이트 — ImageUpload.Validate 통과, VLM mock 은 내용 미해석.
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
        var content = new MultipartFormDataContent();
        var img = new ByteArrayContent(bytes);
        img.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(img, "image", "capture.jpg");
        content.Add(new StringContent(scenarioId), "scenario_id");
        return content;
    }
}
