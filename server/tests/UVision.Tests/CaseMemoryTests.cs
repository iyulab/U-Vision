using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HoneAI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Models;
using UVision.Api.Services.Memory;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 확정 사례 기억 단의 결선 — 켜면 장부 사다리 맨 아래에 System1 단으로 서고(Shadow — 판정 · 응답 v1 은 그대로), 사람이
/// 확정한 라벨이 동기화로 기억에 들어간 뒤에는 같은 이미지에 답한다. 확인(verification) 시나리오에는 서지 않는다.
/// mock 임베더는 같은 바이트만 가깝게 본다 — 이웃 1 로 그 성질을 쓴다.
/// </summary>
public class CaseMemoryTests
{
    private sealed class MemoryEnabledFactory : UVisionApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Provider", "mock");
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Neighbors", "1");
            // 배경 동기화가 테스트 중에 끼지 않게 — 테스트가 SyncOnceAsync 를 직접 부른다.
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:SyncIntervalSeconds", "3600");
        }
    }

    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static MultipartFormDataContent Form(byte[] image, string scenarioId = "demo")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(scenarioId), "scenario_id");
        return form;
    }

    private static byte[] Jpeg(byte tail) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, tail];

    private static async Task<List<ProvenanceRecord>> LedgerAsync(UVisionApiFactory factory, string scenarioId = "demo")
    {
        var records = new List<ProvenanceRecord>();
        await foreach (var r in new JsonlProvenanceSink(Path.Combine(factory.DataPath, scenarioId, "ledger", Today + ".jsonl")).ReadAsync())
            records.Add(r);
        return records;
    }

    [Fact]
    public async Task Enabled_EmptyMemory_StandsAsLowestTier_Abstains_AndV1ResponseIsUnchanged()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();

        var v1 = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(1)));
        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        var body = await v1.Content.ReadFromJsonAsync<JsonElement>();
        // v1 응답의 필드는 기억이 없을 때와 같다 — 기억 단은 응답에 아무것도 더하지 않는다.
        Assert.Equal(
            ["confidence", "findings", "image_id", "timestamp", "verdict"],
            body.EnumerateObject().Select(p => p.Name).Order());

        var p = Assert.Single(await LedgerAsync(factory)).Provenance;
        Assert.Equal("memory", p.Path![0].TierId);
        Assert.Equal(StepOutcome.Abstained, p.Path[0].Outcome);
        Assert.Equal(StepOutcome.Settled, p.Path[^1].Outcome);
        Assert.Equal("vlm", p.TierId);
        Assert.Equal(MockImageEmbedder.Identity.Key, p.Annotations!["memory:encoder"]);
    }

    [Fact]
    public async Task AfterLabelAndSync_RecallsTheConfirmedClass_InShadow_WithDistanceAnnotation()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();
        var image = Jpeg(7);

        var first = await (await client.PostAsync("/api/u-vision/inspect", Form(image))).Content.ReadFromJsonAsync<JsonElement>();
        var imageId = first.GetProperty("image_id").GetString()!;
        var put = await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = "NG" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var memory = factory.Services.GetRequiredService<CaseMemory>();
        Assert.Equal(1, await memory.SyncOnceAsync(CancellationToken.None));
        Assert.Equal(0, await memory.SyncOnceAsync(CancellationToken.None)); // 이미 든 사례는 다시 임베딩하지 않는다

        var v2 = await (await client.PostAsync("/api/u-vision/v2/inspect", Form(image))).Content.ReadFromJsonAsync<JsonElement>();
        var path = v2.GetProperty("path").EnumerateArray().ToList();
        Assert.Equal("memory", path[0].GetProperty("tier").GetString());
        // NG 는 놓치면 안 되는 값 — 기억이 답해도 확정하지 않고 위로 보낸다. 판정은 VLM 이 정한다.
        Assert.Equal("vlm", v2.GetProperty("settled_by").GetProperty("tier").GetString());

        var p = (await LedgerAsync(factory))[^1].Provenance;
        Assert.Equal("memory", p.Path![0].TierId);
        Assert.NotEqual(StepOutcome.Abstained, p.Path[0].Outcome);
        Assert.Equal("0", p.Annotations!["memory:nearest_distance"]);
        Assert.Equal("1", p.Annotations["memory:neighbors"]);
    }

    [Fact]
    public async Task MemoryReport_ShowsStatus_AndReplaysTodaysLedger()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();
        var image = Jpeg(11);

        var before = await (await client.GetAsync("/api/u-vision/memory?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(before.GetProperty("enabled").GetBoolean());
        Assert.False(before.TryGetProperty("encoder", out _)); // 아직 안 지어짐
        Assert.Equal(0, before.GetProperty("replayed").GetInt32());
        Assert.Empty(before.GetProperty("replay").EnumerateArray());

        var imageId = (await (await client.PostAsync("/api/u-vision/inspect", Form(image))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("image_id").GetString()!;
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = "OK" });
        await factory.Services.GetRequiredService<CaseMemory>().SyncOnceAsync(CancellationToken.None);
        var second = (await (await client.PostAsync("/api/u-vision/inspect", Form(image))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("image_id").GetString()!;
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = second, Label = "OK" });

        var report = await (await client.GetAsync("/api/u-vision/memory?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MockImageEmbedder.Identity.Key, report.GetProperty("encoder").GetString());
        Assert.Equal(1, report.GetProperty("cases").GetInt32());
        Assert.Equal(1, report.GetProperty("replayed").GetInt32()); // 첫 판정은 기억이 비어 거리를 남기지 않았다
        var configured = report.GetProperty("replay").EnumerateArray().Single(r => r.GetProperty("configured").GetBoolean());
        Assert.Equal(0.15, configured.GetProperty("max_distance").GetDouble());
        Assert.Equal(1, configured.GetProperty("answered").GetInt32());
        Assert.Equal(1, configured.GetProperty("labeled").GetInt32());
        Assert.Equal(1.0, configured.GetProperty("label_agreement").GetDouble());
        Assert.Equal(0, configured.GetProperty("missed").GetInt32());
    }

    [Fact]
    public async Task MemoryReport_Disabled_IsEnabledFalse_UnknownScenario404_BadDate400()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();

        var off = await (await client.GetAsync("/api/u-vision/memory?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/u-vision/memory?scenario_id=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/u-vision/memory?scenario_id=demo&date=2026-13-40")).StatusCode);
    }

    [Fact]
    public async Task RemovedLabel_LeavesMemory_OnNextSync()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();
        var image = Jpeg(9);
        var imageId = (await (await client.PostAsync("/api/u-vision/inspect", Form(image))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("image_id").GetString()!;
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = "OK" });
        var memory = factory.Services.GetRequiredService<CaseMemory>();
        await memory.SyncOnceAsync(CancellationToken.None);

        var labels = factory.Services.GetRequiredService<Api.Storage.ILabelStore>();
        await labels.DeleteAsync("demo", Today, imageId);
        await memory.SyncOnceAsync(CancellationToken.None);

        var outcome = await memory.RecallAsync(
            Api.Services.Scenarios.EffectiveScenario.Resolve(new Scenario { ScenarioId = "demo", Name = "demo" }, false, true),
            image, CancellationToken.None);
        Assert.Null(outcome.Answer?.Class);
        Assert.Contains("0 < 1", outcome.AbstainReason);
    }

    [Fact]
    public async Task Verification_HasNoMemoryTier_EvenWhenEnabled()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();
        var path = Path.Combine(factory.DataPath, "demo", "scenario.json");
        var scenario = JsonSerializer.Deserialize<Scenario>(await File.ReadAllTextAsync(path), Api.Storage.StoragePaths.Json)!
            with { Type = "verification" };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(scenario, Api.Storage.StoragePaths.Json));

        var form = Form(Jpeg(3));
        form.Add(new StringContent("P-100"), "expected_part");
        var resp = await client.PostAsync("/api/u-vision/v2/inspect", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var p = Assert.Single(await LedgerAsync(factory)).Provenance;
        Assert.DoesNotContain(p.Path!, s => s.TierId == "memory");
        Assert.False(p.Annotations!.ContainsKey("memory:encoder"));
    }

    [Fact]
    public async Task EmbedderFailure_AbstainsWithReason_AndInspectionProceeds()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IImageEmbedder>(new ThrowingEmbedder()))).CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(4)));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var p = Assert.Single(await LedgerAsync(factory)).Provenance;
        Assert.Equal(StepOutcome.Abstained, p.Path![0].Outcome);
        Assert.False(p.Annotations!.ContainsKey("memory:encoder"));
    }

    private sealed class ThrowingEmbedder : IImageEmbedder
    {
        public string Name => "throwing";
        public bool IsEnabled => true;
        public Task<ImageEmbeddings> EmbedAsync(IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("connection refused");
    }
}
