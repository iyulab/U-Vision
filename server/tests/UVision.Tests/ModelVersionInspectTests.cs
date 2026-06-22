using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Services.Ml;
using Xunit;

namespace UVision.Tests;

public sealed class ModelVersionInspectTests
{
    /// <summary>ModelVersion 을 스탬프하는 테스트 분류기 — VLM=OK 와 일치(ok)하며 버전만 부여.</summary>
    private sealed class VersionStampingClassifier : IMlClassifier
    {
        public string Name => "version-stamp";
        public bool IsEnabled => true;
        public Task<MlClassification> ClassifyAsync(
            ReadOnlyMemory<byte> image, string scenarioId, CancellationToken ct = default) =>
            Task.FromResult(new MlClassification
            {
                Label = "ok", Confidence = 0.9,
                Scores = new Dictionary<string, double> { ["ok"] = 0.9, ["ng"] = 0.1 },
                ModelVersion = "v2",
            });
    }

    private static MultipartFormDataContent Multipart()
    {
        var content = new MultipartFormDataContent();
        var img = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        img.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(img, "image", "test.jpg");
        content.Add(new StringContent("demo"), "scenario_id");
        return content;
    }

    [Fact]
    public async Task Inspect_StampsModelVersion_WhenClassifierProvidesIt()
    {
        using var factory = UVisionApiFactory.Create(mlClassifier: new VersionStampingClassifier());
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Multipart());
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("ml", out var ml));
        Assert.Equal("v2", ml.GetProperty("model_version").GetString());
    }

    [Fact]
    public async Task Inspect_OmitsModelVersion_WhenMlDisabled()
    {
        using var factory = new UVisionApiFactory(); // ML none(기본) — VLM 단독
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Multipart());
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        // ML 비활성 → ml 자체가 생략(byte-identical) → model_version 도 없음.
        Assert.False(doc.RootElement.TryGetProperty("ml", out _));
        Assert.DoesNotContain("model_version", json);
    }
}
