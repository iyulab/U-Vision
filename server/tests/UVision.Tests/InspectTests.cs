using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// inspect 엔드포인트 + mock provider 테스트.
/// provider 는 기본 설정(mock)으로 구동되므로 키 불필요.
/// ⚠️ 이 테스트는 plumbing(파이프라인 흐름)을 검증한다. VLM 판정 정확도는 검증하지 않는다(M0.1 영역).
/// (원본: server/tests/test_inspect.py — 6 케이스 동등 이식)
/// </summary>
public class InspectTests : IClassFixture<UVisionApiFactory>
{
    private readonly UVisionApiFactory _factory;

    public InspectTests(UVisionApiFactory factory) => _factory = factory;

    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static MultipartFormDataContent Form(byte[] bytes, string contentType, string scenarioId)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(scenarioId), "scenario_id");
        return form;
    }

    [Fact]
    public async Task MockProvider_IsDeterministic()
    {
        var provider = new MockVlmProvider();
        var scenario = new ScenarioContext { ScenarioId = "demo" };
        var bytes = Encoding.UTF8.GetBytes("identical-bytes");

        var r1 = await provider.InspectAsync(bytes, scenario);
        var r2 = await provider.InspectAsync(bytes, scenario);

        Assert.Equal(r1, r2); // record value equality
        Assert.True(r1.Verdict is Verdict.OK or Verdict.NG);
        Assert.InRange(r1.Confidence, 0.0, 1.0);
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/health");

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Inspect_ReturnsVerdict()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/inspect", Form(Jpeg, "image/jpeg", "demo"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("verdict").GetString() is "OK" or "NG");
        Assert.StartsWith("img_", json.GetProperty("image_id").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("timestamp").GetString()));
        Assert.InRange(json.GetProperty("confidence").GetDouble(), 0.0, 1.0);
    }

    [Fact]
    public async Task Inspect_RejectsNonImage()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync(
            "/api/inspect", Form(Encoding.UTF8.GetBytes("hello"), "text/plain", "demo"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode); // 415
    }

    [Fact]
    public async Task Inspect_RejectsUnknownScenario()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/inspect", Form(Jpeg, "image/jpeg", "ghost"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // 404
    }

    [Fact]
    public async Task Inspect_RejectsEmptyImage()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/inspect", Form([], "image/jpeg", "demo"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 400
    }

    [Fact]
    public async Task Inspect_PersistsResult_RetrievableViaResults()
    {
        var client = _factory.CreateClient();

        var inspectResp = await client.PostAsync("/api/inspect", Form(Jpeg, "image/jpeg", "demo"));
        Assert.Equal(HttpStatusCode.OK, inspectResp.StatusCode);
        var inspectJson = await inspectResp.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = inspectJson.GetProperty("image_id").GetString();

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var resultsResp = await client.GetAsync($"/api/results?scenario_id=demo&date={today}");
        Assert.Equal(HttpStatusCode.OK, resultsResp.StatusCode);

        var results = await resultsResp.Content.ReadFromJsonAsync<JsonElement>();
        // 방금 영속화한 image_id 가 조회 결과에 포함되고, 응답 stem 과 일치해야 한다.
        var match = results.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("image_id").GetString() == imageId);
        Assert.Equal(JsonValueKind.Object, match.ValueKind);
        Assert.Equal($"{imageId}.jpg", match.GetProperty("image_file").GetString());
        Assert.True(match.GetProperty("verdict").GetString() is "OK" or "NG");
    }

    [Fact]
    public async Task Results_DefaultsToToday_WhenDateOmitted()
    {
        var client = _factory.CreateClient();
        await client.PostAsync("/api/inspect", Form(Jpeg, "image/jpeg", "demo"));

        // date 생략 → 오늘(UTC). 방금 저장한 레코드가 보여야 한다.
        var resp = await client.GetAsync("/api/results?scenario_id=demo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var results = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(results.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Inspect_RejectsMalformedScenarioId()
    {
        var client = _factory.CreateClient();
        // 경로 주입 시도("../") → sanitize 거부 → 400(존재하지 않음 404 와 구분).
        var resp = await client.PostAsync("/api/inspect", Form(Jpeg, "image/jpeg", "../etc"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 400
    }

    [Fact]
    public async Task Inspect_Returns500_WhenPersistenceFails()
    {
        // must-succeed 계약: 영속화 실패 시 200 위장 금지(→500).
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
                s.AddSingleton<IInspectionStore, ThrowingInspectionStore>())).CreateClient();

        var resp = await client.PostAsync("/api/inspect", Form(Jpeg, "image/jpeg", "demo"));
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode); // 500
    }

    /// <summary>영속화가 항상 실패하는 stub — must-succeed 500 경로 검증용.</summary>
    private sealed class ThrowingInspectionStore : IInspectionStore
    {
        public Task SaveAsync(
            ReadOnlyMemory<byte> image, string ext, StoredResult result,
            CancellationToken cancellationToken = default) =>
            throw new IOException("디스크 쓰기 실패(테스트)");

        public Task<IReadOnlyList<StoredResult>> ListAsync(
            string scenarioId, string date, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredResult>>([]);
    }
}
