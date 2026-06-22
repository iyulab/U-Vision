using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Ml;
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

    private static MultipartFormDataContent FormWithDevice(
        byte[] bytes, string scenarioId, string deviceId, string deviceLabel)
    {
        var form = Form(bytes, "image/jpeg", scenarioId);
        form.Add(new StringContent(deviceId), "device_id");
        form.Add(new StringContent(deviceLabel), "device_label");
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
        var resp = await client.GetAsync("/api/u-vision/health");

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Inspect_ReturnsVerdict()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));

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
            "/api/u-vision/inspect", Form(Encoding.UTF8.GetBytes("hello"), "text/plain", "demo"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode); // 415
    }

    [Fact]
    public async Task Inspect_RejectsUnknownScenario()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "ghost"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // 404
    }

    [Fact]
    public async Task Inspect_RejectsEmptyImage()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/u-vision/inspect", Form([], "image/jpeg", "demo"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 400
    }

    [Fact]
    public async Task Inspect_PersistsResult_RetrievableViaResults()
    {
        var client = _factory.CreateClient();

        var inspectResp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));
        Assert.Equal(HttpStatusCode.OK, inspectResp.StatusCode);
        var inspectJson = await inspectResp.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = inspectJson.GetProperty("image_id").GetString();

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var resultsResp = await client.GetAsync($"/api/u-vision/results?scenario_id=demo&date={today}");
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
        await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));

        // date 생략 → 오늘(UTC). 방금 저장한 레코드가 보여야 한다.
        var resp = await client.GetAsync("/api/u-vision/results?scenario_id=demo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var results = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(results.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Inspect_RejectsMalformedScenarioId()
    {
        var client = _factory.CreateClient();
        // 경로 주입 시도("../") → sanitize 거부 → 400(존재하지 않음 404 와 구분).
        var resp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "../etc"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 400
    }

    [Fact]
    public async Task Inspect_Returns500_WhenPersistenceFails()
    {
        // must-succeed 계약: 영속화 실패 시 200 위장 금지(→500).
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
                s.AddSingleton<IInspectionStore, ThrowingInspectionStore>())).CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode); // 500
    }

    // --- 결과 조회 UI 지원 엔드포인트(무인증 읽기) ---------------------------

    [Fact]
    public async Task ResultDates_ListsDates_AfterInspect()
    {
        var client = _factory.CreateClient();
        await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));

        var resp = await client.GetAsync("/api/u-vision/results/dates?scenario_id=demo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var dates = await resp.Content.ReadFromJsonAsync<string[]>();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Contains(today, dates!);
    }

    [Fact]
    public async Task ResultImage_ServesStoredCapture()
    {
        var client = _factory.CreateClient();
        var inspectResp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));
        var inspectJson = await inspectResp.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = inspectJson.GetProperty("image_id").GetString();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var resp = await client.GetAsync(
            $"/api/u-vision/results/image?scenario_id=demo&date={today}&image_id={imageId}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/jpeg", resp.Content.Headers.ContentType?.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(Jpeg, bytes); // 업로드 원본 바이트 그대로 서빙
    }

    [Fact]
    public async Task ResultImage_Returns404_WhenMissing()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/u-vision/results/image?scenario_id=demo&date=2099-01-01&image_id=img_none0001");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // 404
    }

    [Fact]
    public async Task ResultImage_Returns400_OnMalformedDate()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            "/api/u-vision/results/image?scenario_id=demo&date=not-a-date&image_id=img_x");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 400
    }

    [Fact]
    public async Task ResultDates_Returns400_OnMalformedScenarioId()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/u-vision/results/dates?scenario_id=../etc");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 400 — 경로 주입 차단
    }

    [Fact]
    public async Task Inspect_ImageId_IsFullGuid_AndUnique()
    {
        var client = _factory.CreateClient();
        var r1 = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));
        var r2 = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));
        var id1 = (await r1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("image_id").GetString()!;
        var id2 = (await r2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("image_id").GetString()!;

        Assert.StartsWith("img_", id1);
        Assert.Equal(36, id1.Length); // "img_"(4) + GUID:N(32 hex)
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task Inspect_PersistsDeviceFields()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/u-vision/inspect",
            FormWithDevice(Jpeg, "demo", "uuid-xyz", "라인 B"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var imageId = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("image_id").GetString();

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var results = await (await client.GetAsync($"/api/u-vision/results?scenario_id=demo&date={today}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var match = results.EnumerateArray()
            .First(r => r.GetProperty("image_id").GetString() == imageId);
        Assert.Equal("uuid-xyz", match.GetProperty("device_id").GetString());
        Assert.Equal("라인 B", match.GetProperty("device_label").GetString());
    }

    [Fact]
    public async Task Inspect_WithoutDeviceFields_StillSucceeds_EmptyDevice()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // 구 클라 호환(필드 누락)
        var imageId = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("image_id").GetString();

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var results = await (await client.GetAsync($"/api/u-vision/results?scenario_id=demo&date={today}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var match = results.EnumerateArray()
            .First(r => r.GetProperty("image_id").GetString() == imageId);
        Assert.Equal("", match.GetProperty("device_id").GetString());
    }

    // A3 degrade 가시성: ML enabled 인데 분류 실패 → VLM 단독 진행(degrade), wire 무변경.
    // ml/agreement/requires_review 는 생략(비활성과 동일한 응답 형태)이되, 실패는 삼켜져 200.
    [Fact]
    public async Task Inspect_MlClassifierThrows_DegradesToVlmOnly_Wire200()
    {
        await using var factory = UVisionApiFactory.Create(mlClassifier: new ThrowingMlClassifier());
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));

        resp.EnsureSuccessStatusCode(); // degrade 는 판정을 막지 않는다(200).
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("verdict", out _));        // VLM verdict 정상
        // wire 계약(WhenWritingNull): degrade 시 ml/agreement/requires_review 필드가 아예 부재해야 한다(null 값 아님).
        Assert.False(body.TryGetProperty("ml", out _));               // degrade: ml field omitted from wire
        Assert.False(body.TryGetProperty("agreement", out _));        // degrade: agreement field omitted
        Assert.False(body.TryGetProperty("requires_review", out _));  // degrade: requires_review field omitted
    }

    // A3: ML mock(저신뢰) + 임계>0 + 일치라도 VLM 은 게이팅 제외, ML 저신뢰가 검토 유발.
    // calibrator 가 라이브 inspect 경로에 실제 배선됐는지 검증(배선 회귀).
    // (xUnit IClassFixture 는 단일 생성자를 요구하므로 UVisionApiFactory.Create() 팩토리 메서드 사용.)
    [Fact]
    public async Task Inspect_MlLowConfidence_WithThreshold_RequiresReview()
    {
        await using var factory = UVisionApiFactory.Create(
            mlProvider: "mock", reviewThreshold: 0.80, mlConfidence: 0.50);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg, "image/jpeg", "demo"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("requires_review").GetBoolean());
    }

    /// <summary>IsEnabled=true 이지만 분류 시 항상 throw — degrade 경로 검증용.</summary>
    private sealed class ThrowingMlClassifier : IMlClassifier
    {
        public string Name => "throwing";
        public bool IsEnabled => true;
        public Task<MlClassification> ClassifyAsync(
            ReadOnlyMemory<byte> image, string scenarioId, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated mloop 401");
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

        public Task<InspectionImage?> ReadImageAsync(
            string scenarioId, string date, string imageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<InspectionImage?>(null);

        public Task<IReadOnlyList<string>> ListDatesAsync(
            string scenarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
