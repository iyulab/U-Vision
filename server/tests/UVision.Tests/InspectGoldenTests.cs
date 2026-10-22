using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// <c>/inspect</c> 의 바깥 동작을 byte 단위로 고정하는 특성화 테스트 — 응답 본문, 영속 레코드
/// (<c>{imageId}.json</c>), 메트릭 행. 매 호출마다 바뀌는 <c>image_id</c>·타임스탬프만 정규화하고
/// 필드 순서·생략·숫자 표기는 그대로 비교한다. 판정 파이프라인을 재배치할 때 wire 가 움직이지 않았다는
/// 증거가 이 파일이다(<see cref="WireContractTests"/> 는 DTO 단위 직렬화만 본다).
/// </summary>
public partial class InspectGoldenTests
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static MultipartFormDataContent Form(string deviceId = "tab-1", string deviceLabel = "라인1")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        form.Add(new StringContent(deviceId), "device_id");
        form.Add(new StringContent(deviceLabel), "device_label");
        return form;
    }

    [GeneratedRegex("img_[0-9a-f]{32}")]
    private static partial Regex ImageIdPattern();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z")]
    private static partial Regex TimestampPattern();

    // 들여쓰기 저장 JSON 의 줄바꿈은 플랫폼 기본값을 따른다(Windows CRLF) — 비교는 LF 로.
    private static string Normalize(string s) =>
        TimestampPattern().Replace(ImageIdPattern().Replace(s, "img_ID"), "TS").ReplaceLineEndings("\n");

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<(string Body, string? Stored, string? Metrics)> InspectAsync(UVisionApiFactory factory, HttpClient client)
    {
        var resp = await client.PostAsync("/api/u-vision/inspect", Form());
        var body = await resp.Content.ReadAsStringAsync();
        var dayDir = Path.Combine(factory.DataPath, "demo", Today);
        var stored = Directory.Exists(dayDir)
            ? Directory.GetFiles(dayDir, "img_*.json").Where(f => !f.EndsWith(".label.json")).Select(File.ReadAllText).SingleOrDefault()
            : null;
        var metricsFile = Path.Combine(factory.DataPath, "demo", "metrics", Today + ".jsonl");
        var metrics = File.Exists(metricsFile) ? File.ReadAllText(metricsFile) : null;
        return (Normalize(body), stored is null ? null : Normalize(stored), metrics is null ? null : Normalize(metrics));
    }

    [Fact]
    public async Task VlmOnly_ResponseAndRecord_AreByteStable()
    {
        using var factory = new UVisionApiFactory();
        var (body, stored, metrics) = await InspectAsync(factory, factory.CreateClient());

        Assert.Equal(ExpectedVlmOnlyBody, body);
        Assert.Equal(ExpectedVlmOnlyStored, stored);
        Assert.Null(metrics); // ML 비활성 → 메트릭 부수효과 없음
    }

    [Fact]
    public async Task MlEnabled_ResponseRecordAndMetrics_AreByteStable()
    {
        using var factory = new MlEnabledFactory();
        var (body, stored, metrics) = await InspectAsync(factory, factory.CreateClient());

        Assert.Equal(ExpectedMlBody, body);
        Assert.Equal(ExpectedMlStored, stored);
        Assert.Equal(ExpectedMlMetrics, metrics);
    }

    [Fact]
    public async Task VlmDown_MlEnabled_FailClosedBodyAndMetrics_AreByteStable()
    {
        using var factory = new MlEnabledFactory();
        var client = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IVlmProvider>(new ThrowingVlmProvider()))).CreateClient();
        var (body, stored, metrics) = await InspectAsync(factory, client);

        Assert.Equal(ExpectedFailClosedBody, body);
        Assert.Null(stored); // fail-closed → 레코드 미저장
        Assert.Equal(ExpectedFailClosedMetrics, metrics);
    }

    private sealed class ThrowingVlmProvider : IVlmProvider
    {
        public string Name => "throwing-vlm";
        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated VLM endpoint down");
    }

    private static readonly string ExpectedVlmOnlyBody = """
        {"verdict":"NG","findings":"모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님).","confidence":0.79,"timestamp":"TS","image_id":"img_ID"}
        """.ReplaceLineEndings("\n");
    private static readonly string ExpectedVlmOnlyStored = """
        {
          "scenario_id": "demo",
          "image_id": "img_ID",
          "verdict": "NG",
          "findings": "모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님).",
          "confidence": 0.79,
          "timestamp": "TS",
          "image_file": "img_ID.jpg",
          "device_id": "tab-1",
          "device_label": "라인1"
        }
        """.ReplaceLineEndings("\n");
    private static readonly string ExpectedMlBody = """
        {"verdict":"NG","findings":"모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님).","confidence":0.79,"timestamp":"TS","image_id":"img_ID","ml":{"label":"ng","confidence":0.6922},"agreement":true,"requires_review":false}
        """.ReplaceLineEndings("\n");
    private static readonly string ExpectedMlStored = """
        {
          "scenario_id": "demo",
          "image_id": "img_ID",
          "verdict": "NG",
          "findings": "모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님).",
          "confidence": 0.79,
          "timestamp": "TS",
          "image_file": "img_ID.jpg",
          "device_id": "tab-1",
          "device_label": "라인1",
          "ml": {
            "label": "ng",
            "confidence": 0.6922
          },
          "agreement": true,
          "requires_review": false
        }
        """.ReplaceLineEndings("\n");
    private static readonly string ExpectedMlMetrics = """
        {"image_id":"img_ID","timestamp":"TS","verdict":"NG","vlm_confidence":0.79,"ml_label":"ng","ml_confidence":0.6922,"agreement":true,"requires_review":false,"ml_degraded":false}
        """.ReplaceLineEndings("\n") + "\n";
    private static readonly string ExpectedFailClosedBody = """
        {"detection_unavailable":true,"reason":"vlm_unavailable","ml_hint":{"label":"ng","confidence":0.6922}}
        """.ReplaceLineEndings("\n");
    private static readonly string ExpectedFailClosedMetrics = """
        {"image_id":"img_ID","timestamp":"TS","verdict":null,"vlm_confidence":null,"ml_label":"ng","ml_confidence":0.6922,"agreement":null,"requires_review":null,"ml_degraded":false,"posture":"fail_closed"}
        """.ReplaceLineEndings("\n") + "\n";
}
