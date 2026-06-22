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
/// ③.5 E2 fail-closed — VLM 검출원 실패 시 503 + detection_unavailable 본문(+ML 활성 시 ml_hint),
/// StoredResult 미저장, ML 활성 시 fail-closed 메트릭 행 기록.
/// </summary>
public class FailClosedEndpointTests
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static MultipartFormDataContent Form(string scenarioId)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(scenarioId), "scenario_id");
        return form;
    }

    private sealed class ThrowingVlmProvider : IVlmProvider
    {
        public string Name => "throwing-vlm";
        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated VLM endpoint down");
    }

    private static HttpClient ClientWithDownVlm(UVisionApiFactory factory) =>
        factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IVlmProvider>(new ThrowingVlmProvider())))
            .CreateClient();

    [Fact]
    public async Task VlmDown_Returns503_DetectionUnavailable()
    {
        using var factory = new UVisionApiFactory();
        var resp = await ClientWithDownVlm(factory).PostAsync("/api/u-vision/inspect", Form("demo"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("detection_unavailable").GetBoolean());
        Assert.Equal("vlm_unavailable", json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task VlmDown_MlEnabled_IncludesMlHint()
    {
        using var factory = new MlEnabledFactory();
        var resp = await ClientWithDownVlm(factory).PostAsync("/api/u-vision/inspect", Form("demo"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("ml_hint", out var hint));
        Assert.True(hint.GetProperty("label").GetString() is "ok" or "ng");
    }

    [Fact]
    public async Task VlmDown_MlDisabled_OmitsMlHint()
    {
        using var factory = new UVisionApiFactory();  // ML none
        var resp = await ClientWithDownVlm(factory).PostAsync("/api/u-vision/inspect", Form("demo"));

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("ml_hint", out _));
    }

    [Fact]
    public async Task VlmDown_DoesNotPersistResult()
    {
        using var factory = new UVisionApiFactory();
        var client = ClientWithDownVlm(factory);
        await client.PostAsync("/api/u-vision/inspect", Form("demo"));

        // 정상 클라(VLM mock)로 결과 목록 조회 — fail-closed 는 저장되지 않아야 한다.
        var list = await factory.CreateClient().GetFromJsonAsync<JsonElement>(
            "/api/u-vision/results?scenario_id=demo");
        Assert.Equal(0, list.GetArrayLength());
    }
}
