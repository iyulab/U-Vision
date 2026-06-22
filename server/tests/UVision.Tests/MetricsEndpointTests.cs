using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Ml;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// B3 메트릭 배선 e2e — inspect 경로가 ML 활성 시 예측 신호를 메트릭 시계열에 흘리고,
/// degrade(분류 실패)를 구분 기록하며, ML 비활성(기본)일 땐 메트릭 부수효과가 전혀 없다(byte-freeze).
/// 집계(rate·NG recall)는 C2 — 여기선 write 배선만 검증한다.
/// </summary>
public class MetricsEndpointTests
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

    private static string TodayUtc() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Task<IReadOnlyList<MetricsRow>> ReadMetrics(UVisionApiFactory factory) =>
        factory.Services.GetRequiredService<IMetricsStore>().ReadAsync("demo", TodayUtc());

    [Fact]
    public async Task MlEnabled_Inspect_WritesMetricsRow()
    {
        await using var factory = UVisionApiFactory.Create(mlProvider: "mock");
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = body.GetProperty("image_id").GetString();

        var rows = await ReadMetrics(factory);
        var row = Assert.Single(rows);
        Assert.Equal(imageId, row.ImageId);
        // 메트릭 신호가 응답/판정과 정합해야 한다.
        Assert.Equal(body.GetProperty("verdict").GetString(), row.Verdict.ToString());
        Assert.Equal(body.GetProperty("agreement").GetBoolean(), row.Agreement);
        Assert.Equal(body.GetProperty("requires_review").GetBoolean(), row.RequiresReview);
        Assert.Equal(body.GetProperty("ml").GetProperty("label").GetString(), row.MlLabel);
        Assert.False(row.MlDegraded);
    }

    [Fact]
    public async Task MlEnabled_ClassifierThrows_WritesDegradedRow()
    {
        await using var factory = UVisionApiFactory.Create(mlClassifier: new ThrowingMlClassifier());
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));
        resp.EnsureSuccessStatusCode(); // degrade 는 판정을 막지 않는다.

        // degrade 도 메트릭에 남아야 한다(A3 가 만든 '실패↔비활성' 구분 → degrade율 토대).
        var row = Assert.Single(await ReadMetrics(factory));
        Assert.True(row.MlDegraded);
        Assert.Null(row.MlLabel);
        Assert.Null(row.MlConfidence);
        Assert.Null(row.Agreement);
        Assert.Null(row.RequiresReview);
    }

    [Fact]
    public async Task MlDisabled_Inspect_WritesNoMetrics_SideEffectFreeze()
    {
        // 기본 팩토리 = ML none(①단계) → 메트릭 파일조차 생기지 않아야 한다(② 이전 부수효과 동결).
        await using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));
        resp.EnsureSuccessStatusCode();

        Assert.Empty(await ReadMetrics(factory));
        Assert.False(Directory.Exists(Path.Combine(factory.DataPath, "demo", "metrics")));
    }

    [Fact]
    public async Task GetMetrics_AggregatesInspections()
    {
        await using var factory = UVisionApiFactory.Create(mlProvider: "mock");
        var client = factory.CreateClient();

        await client.PostAsync("/api/u-vision/inspect", Form("demo"));
        await client.PostAsync("/api/u-vision/inspect", Form("demo"));

        var summary = await client.GetFromJsonAsync<JsonElement>(
            $"/api/u-vision/metrics?scenario_id=demo&date={TodayUtc()}");

        Assert.Equal(2, summary.GetProperty("inspections").GetInt32());
        Assert.Equal(0, summary.GetProperty("ml_degraded").GetInt32());
        // 임계 0(기본) → agreement ⟺ !requires_review. 비율 필드 존재(비-degrade 2건이라 non-null).
        Assert.Equal(JsonValueKind.Number, summary.GetProperty("agreement_rate").ValueKind);
        Assert.Equal("demo", summary.GetProperty("scenario_id").GetString());
    }

    [Fact]
    public async Task GetMetrics_NgRecall_JoinsHumanLabels()
    {
        await using var factory = UVisionApiFactory.Create(mlProvider: "mock");
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form("demo"));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = body.GetProperty("image_id").GetString();

        // 사람이 NG 로 라벨(정답) → NG recall 분모 1.
        var label = new { scenario_id = "demo", date = TodayUtc(), image_id = imageId, label = "NG" };
        var put = await client.PutAsJsonAsync("/api/u-vision/results/label", label);
        put.EnsureSuccessStatusCode();

        var summary = await client.GetFromJsonAsync<JsonElement>(
            $"/api/u-vision/metrics?scenario_id=demo&date={TodayUtc()}");

        Assert.Equal(1, summary.GetProperty("labeled").GetInt32());
        Assert.Equal(1, summary.GetProperty("labeled_ng").GetInt32());
        // VLM recall = vlm_ng_hits/labeled_ng — verdict 가 NG 면 1.0, OK 면 0.0. null 아님(분모 1).
        Assert.Equal(JsonValueKind.Number, summary.GetProperty("vlm_ng_recall").ValueKind);
    }

    [Fact]
    public async Task GetMetrics_NoData_Returns200EmptySummary()
    {
        // ML 비활성·미검사 → 404 가 아니라 0 집계(정직히 "데이터 없음").
        await using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var summary = await client.GetFromJsonAsync<JsonElement>(
            $"/api/u-vision/metrics?scenario_id=demo&date={TodayUtc()}");

        Assert.Equal(0, summary.GetProperty("inspections").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("agreement_rate").ValueKind);
    }

    /// <summary>IsEnabled=true 이지만 분류 시 항상 throw — degrade 메트릭 경로 검증용.</summary>
    private sealed class ThrowingMlClassifier : IMlClassifier
    {
        public string Name => "throwing";
        public bool IsEnabled => true;
        public Task<MlClassification> ClassifyAsync(
            ReadOnlyMemory<byte> image, string scenarioId, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated mloop 401");
    }
}
