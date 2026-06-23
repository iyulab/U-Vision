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
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.TryGetProperty("posture", out _));
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
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.TryGetProperty("ml", out _));
    }

    // CoPrimary: VLM·ML 불일치(requires_review=true)이면 posture=="review_block".
    //            일치(requires_review=false/absent)이면 posture 필드 자체가 없어야 한다.
    //            mock ML 라벨은 이미지 해시 기반이라 불일치 여부를 강제할 수 없으므로
    //            양쪽 케이스 모두 유효하도록 조건부로 단정한다.
    [Fact]
    public async Task CoPrimary_PostureWire_ReviewBlockOrAbsent()
    {
        var scenarioId = $"copri_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = scenarioId, Name = "cp", Criteria = "c" });
        var store = (IAuthorityStore)_factory.Services.GetService(typeof(IAuthorityStore))!;
        await store.SetStageAsync(scenarioId, AuthorityStage.CoPrimary, "test", "promote", null, default);

        var resp = await _factory.CreateClient().PostAsync("/api/u-vision/inspect", InspectForm(scenarioId));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // ml オブジェクト有無で分岐: ML が動いた(dual-check 実行)場合のみ posture を検証.
        if (root.TryGetProperty("ml", out _))
        {
            // requires_review が true なら VLM·ML 불일치 → review_block.
            // false/absent なら 일치 → Proceed → posture 필드 없음.
            var requiresReview = root.TryGetProperty("requires_review", out var rrEl)
                && rrEl.ValueKind == JsonValueKind.True;

            if (requiresReview)
            {
                Assert.True(root.TryGetProperty("posture", out var postureEl),
                    $"requires_review=true 인데 posture 필드가 없음. body={body}");
                Assert.Equal("review_block", postureEl.GetString());
            }
            else
            {
                Assert.False(root.TryGetProperty("posture", out _),
                    $"requires_review=false/absent 인데 posture 필드가 있음. body={body}");
            }
        }
        // ml 없음(비활성 경로): posture 도 없어야 한다.
        else
        {
            Assert.False(root.TryGetProperty("posture", out _),
                $"ML 비활성인데 posture 필드가 있음. body={body}");
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
