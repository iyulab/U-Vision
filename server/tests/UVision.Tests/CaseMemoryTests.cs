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

    /// <summary>OK 라벨 둘이 든 하루를 만든다 — 첫 판정은 기억이 비어 거리를 남기지 않아 재생 건은 하나다.</summary>
    private static async Task SeedTwoLabeledOkAsync(MemoryEnabledFactory factory, HttpClient client)
    {
        var image = Jpeg(11);
        var first = (await (await client.PostAsync("/api/u-vision/inspect", Form(image))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("image_id").GetString()!;
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = first, Label = "OK" });
        await factory.Services.GetRequiredService<CaseMemory>().SyncOnceAsync(CancellationToken.None);
        var second = (await (await client.PostAsync("/api/u-vision/inspect", Form(image))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("image_id").GetString()!;
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = second, Label = "OK" });
    }

    [Fact]
    public async Task MemoryReport_Recommendation_SaysWhyThereIsNoBasisWithoutDefectLabels()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();

        var empty = await (await client.GetAsync("/api/u-vision/memory?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(empty.TryGetProperty("recommendation", out _)); // 재생이 없으면 추천도 없다
        Assert.False(empty.TryGetProperty("days", out _));           // 기본 하루면 days 를 내지 않는다

        await SeedTwoLabeledOkAsync(factory, client);
        var report = await (await client.GetAsync("/api/u-vision/memory?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        var rec = report.GetProperty("recommendation");

        Assert.False(rec.TryGetProperty("max_distance", out _));
        Assert.False(rec.TryGetProperty("defect_pass_upper_95", out _));
        Assert.False(rec.GetProperty("sufficient").GetBoolean());
        Assert.Equal(0, rec.GetProperty("labeled_defects").GetInt32());
        Assert.Equal(0.02, rec.GetProperty("target_miss_rate").GetDouble()); // 시나리오 목표값 기본
        Assert.Equal(149, rec.GetProperty("defects_needed").GetInt32());
        Assert.Contains("불량 라벨", rec.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Flywheel_BucketsLabeledJudgmentsInOrder_AndTheMemoryLineStartsWhenMemoryExists()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();
        await SeedTwoLabeledOkAsync(factory, client);

        var report = await (await client.GetAsync("/api/u-vision/flywheel?scenario_id=demo&bucket=1")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, report.GetProperty("labeled").GetInt32());
        Assert.Equal(0.15, report.GetProperty("memory_max_distance").GetDouble());
        Assert.Equal(30, report.GetProperty("days").GetInt32());
        var points = report.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([1, 2], points.Select(p => p.GetProperty("labeled_through").GetInt32()));
        // 첫 판정은 기억이 비어 답하지 못했고, 같은 사진을 다시 본 둘째는 기억이 답했다.
        Assert.Equal([0, 1], points.Select(p => p.GetProperty("memory_answered").GetInt32()));
        Assert.Equal(1.0, points[1].GetProperty("memory_agreement").GetDouble());
        Assert.False(points[0].TryGetProperty("memory_agreement", out _));     // 답한 건이 없으면 비율을 생략한다(0 으로 꾸미지 않는다)
        Assert.False(points[0].TryGetProperty("operative_defect_pass", out _)); // 사람 라벨 불량이 없다
    }

    [Fact]
    public async Task Flywheel_RejectsBadInput_AndUnknownScenarios()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();

        foreach (var bad in new[] { "bucket=0", "bucket=501", "days=0", "days=91", "date=garbage" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/u-vision/flywheel?scenario_id=demo&{bad}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/u-vision/flywheel?scenario_id=nope")).StatusCode);
        var empty = await (await client.GetAsync("/api/u-vision/flywheel?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, empty.GetProperty("labeled").GetInt32());
        Assert.Empty(empty.GetProperty("points").EnumerateArray());
    }

    [Fact]
    public async Task ScenarioMemoryMaxDistance_OverridesTheInstallationThreshold_InTheJudgeAndInTheReplay()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();
        var first = (await (await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(7)))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("image_id").GetString()!;
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = first, Label = "OK" });
        await factory.Services.GetRequiredService<CaseMemory>().SyncOnceAsync(CancellationToken.None);

        // 다른 바이트 — mock 임베더에서 설치 문턱(0.15) 밖이라 기억이 기권한다.
        await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(9)));
        Assert.Equal(StepOutcome.Abstained, (await LedgerAsync(factory))[^1].Provenance.Path![0].Outcome);

        // 시나리오가 문턱을 코사인 거리의 끝(2)으로 두면 같은 사진에 기억이 답한다 — 설치 값은 그대로다.
        factory.SeedScenario(new Scenario
        {
            ScenarioId = "demo", Name = "데모 검사", Criteria = "기준", MemoryMaxDistance = 2,
        });
        await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(9)));
        Assert.NotEqual(StepOutcome.Abstained, (await LedgerAsync(factory))[^1].Provenance.Path![0].Outcome);

        // 재생 표의 «지금 설정값» 행도 시나리오 값을 따른다.
        var report = await (await client.GetAsync("/api/u-vision/memory?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        var configured = report.GetProperty("replay").EnumerateArray().Single(r => r.GetProperty("configured").GetBoolean());
        Assert.Equal(2.0, configured.GetProperty("max_distance").GetDouble());
    }

    [Fact]
    public async Task MemoryReport_Days_ReadsEarlierDaysToo_AndRejectsABadWindow()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();
        await SeedTwoLabeledOkAsync(factory, client);

        // 어제 폴더 = 오늘 폴더의 복사(기록 · 라벨 사이드카 · 장부) — 두 날의 재생이 합쳐지는지 본다.
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var scenarioDir = Path.Combine(factory.DataPath, "demo");
        Directory.CreateDirectory(Path.Combine(scenarioDir, yesterday));
        foreach (var file in Directory.GetFiles(Path.Combine(scenarioDir, Today)))
            File.Copy(file, Path.Combine(scenarioDir, yesterday, Path.GetFileName(file)));
        File.Copy(Path.Combine(scenarioDir, "ledger", Today + ".jsonl"), Path.Combine(scenarioDir, "ledger", yesterday + ".jsonl"));

        var one = await (await client.GetAsync("/api/u-vision/memory?scenario_id=demo")).Content.ReadFromJsonAsync<JsonElement>();
        var two = await (await client.GetAsync("/api/u-vision/memory?scenario_id=demo&days=2")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(one.GetProperty("replayed").GetInt32() * 2, two.GetProperty("replayed").GetInt32());
        Assert.Equal(2, two.GetProperty("days").GetInt32());
        foreach (var bad in new[] { "days=0", "days=91", "days=-1", "days=2&date=2026-13-40", "days=2&date=garbage" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/u-vision/memory?scenario_id=demo&{bad}")).StatusCode);
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
    public async Task Sync_SkipsConflictedLabels_AndLabelsWithoutImage()
    {
        using var factory = new MemoryEnabledFactory();
        var client = factory.CreateClient();
        var imageId = (await (await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(21)))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("image_id").GetString()!;
        await client.PutAsJsonAsync("/api/u-vision/results/label", new LabelInput
        { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = "OK" });
        // 블라인드 감사가 다른 답 — 감사 충돌이 풀릴 때까지 확정 사례가 아니다.
        var audit = await client.PostAsJsonAsync("/api/u-vision/results/audit", new
        { scenario_id = "demo", date = Today, image_id = imageId, label = "NG" });
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        // 짝 이미지가 없는 라벨(지워진 레코드) — 건너뛴다.
        await factory.Services.GetRequiredService<Api.Storage.ILabelStore>()
            .AppendLabelAsync("demo", Today, "img_gone", "OK", "test");

        var memory = factory.Services.GetRequiredService<CaseMemory>();
        Assert.Equal(0, await memory.SyncOnceAsync(CancellationToken.None));
        // 판은 검사에서 이미 배웠으니 기억은 지어지되 비어 있다.
        Assert.Equal(0, memory.StatusOf("demo")!.Value.Cases);
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

    [Fact]
    public async Task EmbedderTimeout_AbstainsLikeAnyFailure_AndInspectionProceeds()
    {
        // HttpClient.Timeout 은 요청 토큰이 아니라 TaskCanceledException(안에 TimeoutException)으로 끝난다 — 판정 취소가
        // 아니므로 기권해야 한다(취소로 보고 던지면 /inspect 가 실패한다).
        using var factory = new MemoryEnabledFactory();
        var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IImageEmbedder>(new ThrowingEmbedder(
                new TaskCanceledException("timed out", new TimeoutException()))))).CreateClient();

        var resp = await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(4)));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(StepOutcome.Abstained, Assert.Single(await LedgerAsync(factory)).Provenance.Path![0].Outcome);
    }

    private sealed class ThrowingEmbedder(Exception? failure = null) : IImageEmbedder
    {
        public string Name => "throwing";
        public bool IsEnabled => true;
        public Task<ImageEmbeddings> EmbedAsync(IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default) =>
            throw failure ?? new HttpRequestException("connection refused");
    }
}
