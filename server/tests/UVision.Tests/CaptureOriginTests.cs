using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 캡처 출처(B01) — 시험 데이터셋에서 넣은 판정은 그대로 판정 · 기록되고, 출처가 레코드 · v2 응답 · 메트릭 · 장부에 남는다.
/// 출처가 없으면 그 필드는 어디에도 나타나지 않는다(현장 촬영, byte 보존은 골든 테스트가 가드).
/// </summary>
public class CaptureOriginTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

    private static MultipartFormDataContent Form(string? origin = null, string? originRef = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "x.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        if (origin is not null) form.Add(new StringContent(origin), "origin");
        if (originRef is not null) form.Add(new StringContent(originRef), "origin_ref");
        return form;
    }

    [Fact]
    public async Task DatasetOrigin_IsJudgedAsUsual_AndMarkedEverywhere()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/v2/inspect", Form("dataset", "kamp-seq025:0a1b2c3d"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dataset", body.GetProperty("origin").GetString());
        Assert.Equal("kamp-seq025:0a1b2c3d", body.GetProperty("origin_ref").GetString());
        Assert.True(body.TryGetProperty("class", out _)); // 판정은 그대로 났다

        var record = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/u-vision/results?scenario_id=demo")).EnumerateArray());
        Assert.Equal("dataset", record.GetProperty("origin").GetString());
        Assert.Equal("kamp-seq025:0a1b2c3d", record.GetProperty("origin_ref").GetString());

        var metrics = await client.GetFromJsonAsync<JsonElement>("/api/u-vision/metrics?scenario_id=demo");
        Assert.Equal(1, metrics.GetProperty("inspections").GetInt32());
        Assert.Equal(1, metrics.GetProperty("dataset_inspections").GetInt32());

        var ledger = Directory.GetFiles(Path.Combine(factory.DataPath, "demo", "ledger"), "*.jsonl")
            .SelectMany(File.ReadAllLines).Single(l => l.Trim().Length > 0);
        var annotations = JsonDocument.Parse(ledger).RootElement.GetProperty("provenance").GetProperty("annotations");
        Assert.Equal("dataset", annotations.GetProperty("origin:kind").GetString());
        Assert.Equal("kamp-seq025:0a1b2c3d", annotations.GetProperty("origin:ref").GetString());
    }

    [Fact]
    public async Task NoOrigin_LeavesNoTrace()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var body = await (await client.PostAsync("/api/u-vision/v2/inspect", Form())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("origin", out _));
        Assert.False(body.TryGetProperty("origin_ref", out _));
        var record = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/u-vision/results?scenario_id=demo")).EnumerateArray());
        Assert.False(record.TryGetProperty("origin", out _));
        var metrics = await client.GetFromJsonAsync<JsonElement>("/api/u-vision/metrics?scenario_id=demo");
        Assert.Equal(0, metrics.GetProperty("dataset_inspections").GetInt32());
    }

    [Theory]
    [InlineData("live", null)]        // 지어지지 않은 종류
    [InlineData("Dataset", null)]     // 철자 그대로만
    [InlineData(null, "kamp:1")]      // 식별만 있고 종류 없음
    [InlineData("dataset", "a/b")]    // 식별 문자 밖
    [InlineData("dataset", "a b")]
    public async Task InvalidOrigin_Is400(string? origin, string? originRef)
    {
        using var factory = new UVisionApiFactory();
        var resp = await factory.CreateClient().PostAsync("/api/u-vision/v2/inspect", Form(origin, originRef));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public void Validate_AcceptsDatasetWithOrWithoutRef()
    {
        Assert.Null(CaptureOrigin.Validate(null, null));
        Assert.Null(CaptureOrigin.Validate("dataset", null));
        Assert.Null(CaptureOrigin.Validate("dataset", "visa-pcb1:Data.Images.Anomaly.000"));
        Assert.NotNull(CaptureOrigin.Validate("dataset", new string('a', 129)));
    }
}
