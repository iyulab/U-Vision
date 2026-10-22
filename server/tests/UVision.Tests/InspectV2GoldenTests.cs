using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 결과 v2(<c>/v2/inspect</c>)의 바깥 모양을 byte 단위로 고정한다 — 응답 본문(검사 VLM 단독 · ML 활성 · 판정 불가 ·
/// 오투입 확인)과 오투입 확인 레코드. 매 호출마다 바뀌는 id · 시각만 정규화하고 필드 순서 · 생략 · 숫자 표기는
/// 그대로 비교한다. v1 은 <see cref="InspectGoldenTests"/>.
/// </summary>
public partial class InspectV2GoldenTests
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static MultipartFormDataContent Form(string scenarioId = "demo", string? expected = null, string? observed = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(scenarioId), "scenario_id");
        form.Add(new StringContent("tab-1"), "device_id");
        form.Add(new StringContent("라인1"), "device_label");
        if (expected is not null) form.Add(new StringContent(expected), "expected_part");
        if (observed is not null) form.Add(new StringContent(observed), "observed_code");
        return form;
    }

    [GeneratedRegex("(img|dec)_[0-9a-f]{32}")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z")]
    private static partial Regex TimestampPattern();

    private static string Normalize(string s) =>
        TimestampPattern().Replace(IdPattern().Replace(s, "$1_ID"), "TS").ReplaceLineEndings("\n");

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient client, MultipartFormDataContent form)
    {
        var resp = await client.PostAsync("/api/u-vision/v2/inspect", form);
        return (resp.StatusCode, Normalize(await resp.Content.ReadAsStringAsync()));
    }

    private static async Task SeedVerificationAsync(UVisionApiFactory factory)
    {
        factory.CreateClient();
        var dir = Path.Combine(factory.DataPath, "part");
        Directory.CreateDirectory(dir);
        var scenario = new Scenario { ScenarioId = "part", Name = "오투입", Type = ScenarioVocabulary.Verification };
        await File.WriteAllTextAsync(Path.Combine(dir, "scenario.json"), JsonSerializer.Serialize(scenario, StoragePaths.Json));
    }

    [Fact]
    public async Task VlmOnly_IsByteStable()
    {
        using var factory = new UVisionApiFactory();
        var (status, body) = await PostAsync(factory.CreateClient(), Form());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ExpectedVlmOnly, body);
    }

    [Fact]
    public async Task MlEnabled_IsByteStable()
    {
        using var factory = new MlEnabledFactory();
        var (status, body) = await PostAsync(factory.CreateClient(), Form());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ExpectedMl, body);
    }

    [Fact]
    public async Task Unavailable_IsByteStable()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IVlmProvider>(new ThrowingVlmProvider()))).CreateClient();
        var (status, body) = await PostAsync(client, Form());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(ExpectedUnavailable, body);
    }

    [Fact]
    public async Task Verification_ResponseAndRecord_AreByteStable()
    {
        using var factory = new UVisionApiFactory();
        await SeedVerificationAsync(factory);
        var (status, body) = await PostAsync(factory.CreateClient(), Form("part", "P-100", "P-200"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ExpectedVerification, body);

        var dayDir = Path.Combine(factory.DataPath, "part",
            DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var stored = Normalize(File.ReadAllText(Directory.GetFiles(dayDir, "img_*.json").Single()));
        // 원시 문자열 상수의 줄바꿈은 이 소스 파일의 것을 따른다 — 비교는 LF 로.
        Assert.Equal(ExpectedVerificationStored.ReplaceLineEndings("\n"), stored);
    }

    private sealed class ThrowingVlmProvider : IVlmProvider
    {
        public string Name => "throwing-vlm";
        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated VLM endpoint down");
    }

    // --- 고정값 -----------------------------------------------------------------------------

    private const string ExpectedVlmOnly =
        """
        {"decision_id":"img_ID","scenario_id":"demo","timestamp":"TS","class":"NG","settled_by":{"tier":"vlm","kind":"vlm"},"confidence":0.79,"findings":"모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님).","path":[{"tier":"vlm","outcome":"settled"}]}
        """;
    private const string ExpectedMl =
        """
        {"decision_id":"img_ID","scenario_id":"demo","timestamp":"TS","class":"NG","settled_by":{"tier":"vlm","kind":"vlm"},"confidence":0.79,"findings":"모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님).","requires_review":false,"path":[{"tier":"ml","outcome":"escalated"},{"tier":"vlm","outcome":"settled"}]}
        """;
    private const string ExpectedUnavailable =
        """
        {"decision_id":"dec_ID","scenario_id":"demo","timestamp":"TS","path":[{"tier":"vlm","outcome":"abstained"}],"detection_unavailable":true,"reason":"vlm_unavailable"}
        """;
    private const string ExpectedVerification =
        """
        {"decision_id":"img_ID","scenario_id":"part","timestamp":"TS","class":"MISMATCH","settled_by":{"tier":"vlm","kind":"vlm"},"confidence":0.79,"findings":"모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님).","path":[{"tier":"match","outcome":"escalated"},{"tier":"vlm","outcome":"settled"}]}
        """;
    private const string ExpectedVerificationStored =
        """
        {
          "scenario_id": "part",
          "image_id": "img_ID",
          "verdict": "NG",
          "findings": "모의 불량 소견: 표면 결함 의심 영역 검출(mock — 실제 판정 아님).",
          "confidence": 0.79,
          "timestamp": "TS",
          "image_file": "img_ID.jpg",
          "device_id": "tab-1",
          "device_label": "라인1",
          "expected_part": "P-100",
          "observed_code": "P-200"
        }
        """;
}
