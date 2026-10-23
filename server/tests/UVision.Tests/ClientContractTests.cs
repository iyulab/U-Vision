using Microsoft.Kiota.Abstractions;
using UVision.Client;
using UVision.Client.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 생성 클라이언트(<c>UVision.Client</c>)가 실제 엔진과 왕복하는지 — 계약 문서에서 생성한 모양이 wire(snake_case ·
/// <c>"OK"/"NG"</c> · multipart 업로드 · 오류 본문)와 맞는지를 엔진 쪽 테스트 서버로 본다.
/// </summary>
public class ClientContractTests
{
    private static readonly Uri Origin = new("http://localhost/");

    private static UVisionApiClient Client(UVisionApiFactory factory) =>
        UVisionClient.Create(factory.CreateClient(), Origin);

    /// <summary>판정 요청 폼 — 파트 이름은 계약 문서의 필드명 그대로(생성기는 multipart 를 <see cref="MultipartBody"/> 로 받는다).</summary>
    private static MultipartBody Capture(string scenarioId, int size = 64, string? deviceId = null)
    {
        var body = new MultipartBody();
        body.AddOrReplacePart("image", "image/jpeg", new MemoryStream(TestImages.MakeJpeg(size, size)), "capture.jpg");
        body.AddOrReplacePart("scenario_id", "text/plain", scenarioId);
        if (deviceId is not null)
            body.AddOrReplacePart("device_id", "text/plain", deviceId);
        return body;
    }

    [Fact]
    public async Task Health_RoundTrips()
    {
        using var factory = new UVisionApiFactory();
        var health = await Client(factory).Health.GetAsync();
        Assert.NotNull(health);
        Assert.False(string.IsNullOrEmpty(health.Status));
    }

    [Fact]
    public async Task InspectV1_UploadsMultipart_AndReadsVerdict()
    {
        using var factory = new UVisionApiFactory();
        var result = await Client(factory).Inspect.PostAsync(Capture("demo", deviceId: "tab-9"));

        Assert.NotNull(result);
        Assert.Contains(result.Verdict, new Verdict?[] { Verdict.OK, Verdict.NG });
        Assert.False(string.IsNullOrEmpty(result.ImageId));
    }

    [Fact]
    public async Task InspectV2_ReadsClassAndPath()
    {
        using var factory = new UVisionApiFactory();
        var result = await Client(factory).V2.Inspect.PostAsync(Capture("demo"));

        Assert.NotNull(result);
        Assert.Contains(result.Class, new[] { "OK", "NG" });
        Assert.Equal("vlm", result.SettledBy?.Tier);
        var step = Assert.Single(result.Path!);
        Assert.Equal("settled", step.Outcome);
    }

    [Fact]
    public async Task UnknownScenario_SurfacesProblemDetails()
    {
        using var factory = new UVisionApiFactory();
        var error = await Assert.ThrowsAsync<ProblemDetails>(() => Client(factory).Inspect.PostAsync(Capture("missing", size: 8)));
        Assert.Equal(404, error.ResponseStatusCode);
    }

    [Fact]
    public async Task AdminCall_CarriesPinHeader_FromCallerHttpClient()
    {
        using var factory = new PinFactory();
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        var client = UVisionClient.Create(http, Origin);

        var created = await client.Scenarios.PostAsync(new ScenarioInput
        {
            Name = "client made",
            Criteria = "anything",
        });
        var id = created?.ScenarioId;
        Assert.False(string.IsNullOrEmpty(id));

        var listed = await client.Scenarios.GetAsync();
        Assert.Contains(listed!, s => s.ScenarioId == id);
    }

    [Theory]
    [InlineData("http://host/", "/api/u-vision", "http://host/api/u-vision")]
    [InlineData("https://host/vision", "/api/u-vision", "https://host/vision/api/u-vision")]
    [InlineData("https://host/vision/", "engine/", "https://host/vision/engine")]
    [InlineData("http://host", "/", "http://host")]
    public void BaseUrl_JoinsProxyPrefixAndApiBasePath(string address, string apiBasePath, string expected) =>
        Assert.Equal(expected, UVisionClient.BaseUrl(new Uri(address), apiBasePath));
}
