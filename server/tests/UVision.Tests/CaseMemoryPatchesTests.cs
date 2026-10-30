using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HoneAI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Memory;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 패치 이상 점수(Shadow) — 켜면 정상 확정 사례의 패치 기억이 지어지고 검사마다 전역 · 패치 두 점수가 장부 annotation 에
/// 남는다. 판정 · 응답은 그대로여야 하고, 꺼져 있으면(기본) 아무것도 달라지지 않는다. mock 임베더는 같은 바이트만 같게 본다.
/// </summary>
public class CaseMemoryPatchesTests
{
    private sealed class CountingEmbedder(IImageEmbedder inner) : IImageEmbedder
    {
        public int PatchCalls { get; private set; }
        public int PatchedImages { get; private set; }

        /// <summary>검사 질의(한 장씩)가 센 값을 비우고 이후 호출만 센다 — 동기화가 한 일만 보려고.</summary>
        public void Reset() => (PatchCalls, PatchedImages) = (0, 0);
        public string Name => inner.Name;
        public bool IsEnabled => inner.IsEnabled;
        public bool SupportsPatches => inner.SupportsPatches;

        /// <summary>패치 호출에 실려 온 인코더 id 들(시나리오가 고른 것) — 고르지 않았으면 null.</summary>
        public List<string?> Encoders { get; } = [];

        /// <summary>이 인코더 id 로 패치를 부르면 연결 실패를 흉내 낸다(MLoop 에 없는 인코더 · 서버 오류).</summary>
        public string? FailingEncoder { get; set; }

        /// <summary>전역 임베딩 호출 수 — 전역 기억은 언제나 설치 인코더라 인코더 id 를 싣지 않는다.</summary>
        public int GlobalCalls { get; private set; }

        public Task<ImageEmbeddings> EmbedAsync(IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default)
        {
            GlobalCalls++;
            return inner.EmbedAsync(images, cancellationToken);
        }

        public Task<PatchEmbeddings> EmbedPatchesAsync(IReadOnlyList<ReadOnlyMemory<byte>> images, string? encoder = null, CancellationToken cancellationToken = default)
        {
            PatchCalls++;
            PatchedImages += images.Count;
            Encoders.Add(encoder);
            if (encoder is not null && encoder == FailingEncoder)
                throw new HttpRequestException("encoder not installed (test)");
            return inner.EmbedPatchesAsync(images, encoder, cancellationToken);
        }
    }

    private sealed class PatchesFactory(bool patches, int cap = 200, int patchBudget = 0) : UVisionApiFactory
    {
        public CountingEmbedder Embedder { get; } = new(new MockImageEmbedder());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Provider", "mock");
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Neighbors", "1");
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:SyncIntervalSeconds", "3600");
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:Patches", patches ? "true" : "false");
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:PatchBankCap", cap.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting($"{UVisionOptions.SectionName}:Memory:PatchBankPatches", patchBudget.ToString(CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services => services.AddSingleton<IImageEmbedder>(Embedder));
        }
    }

    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static byte[] Jpeg(byte tail) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, tail];

    private static MultipartFormDataContent Form(byte[] image)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        return form;
    }

    private static async Task<string> InspectAsync(HttpClient client, byte[] image)
    {
        var resp = await client.PostAsync("/api/u-vision/inspect", Form(image));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("image_id").GetString()!;
    }

    private static async Task LabelAsync(HttpClient client, string imageId, string label) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/u-vision/results/label",
            new LabelInput { ScenarioId = "demo", Date = Today, ImageId = imageId, Label = label })).StatusCode);

    private static async Task<IReadOnlyDictionary<string, string>> LastAnnotationsAsync(UVisionApiFactory factory)
    {
        var records = new List<ProvenanceRecord>();
        await foreach (var r in new JsonlProvenanceSink(Path.Combine(factory.DataPath, "demo", "ledger", Today + ".jsonl")).ReadAsync())
            records.Add(r);
        return records[^1].Provenance.Annotations!;
    }

    private static double Number(IReadOnlyDictionary<string, string> a, string key) =>
        double.Parse(a[key], CultureInfo.InvariantCulture);

    /// <summary>같은 바이트 둘을 정상, 하나를 불량으로 확정한 기억을 짓고 동기화까지 마친다.</summary>
    private static async Task<HttpClient> SeedAsync(PatchesFactory factory)
    {
        var client = factory.CreateClient();
        await LabelAsync(client, await InspectAsync(client, Jpeg(1)), "OK");
        await LabelAsync(client, await InspectAsync(client, Jpeg(2)), "OK");
        await LabelAsync(client, await InspectAsync(client, Jpeg(3)), "NG");
        factory.Embedder.Reset();
        await factory.Services.GetRequiredService<CaseMemory>().SyncOnceAsync(CancellationToken.None);
        return client;
    }

    [Fact]
    public async Task On_RecordsGlobalAndPatchScoresAgainstTheNormalBank_NotTheDefects()
    {
        using var factory = new PatchesFactory(patches: true);
        var client = await SeedAsync(factory);

        await InspectAsync(client, Jpeg(1));          // 기억에 있는 정상
        var seen = await LastAnnotationsAsync(factory);
        Assert.Equal("2", seen["memory:bank_size"]);   // 불량 확정(3)은 정상 기억에 들지 않는다
        Assert.True(Number(seen, "memory:global_score") < 1e-5);
        Assert.True(Number(seen, "memory:patch_score") < 1e-5);

        await InspectAsync(client, Jpeg(9));          // 본 적 없는 이미지
        var unseen = await LastAnnotationsAsync(factory);
        Assert.True(Number(unseen, "memory:global_score") > 0.01);
        Assert.True(Number(unseen, "memory:patch_score") > 0.01);
    }

    [Fact]
    public async Task On_DoesNotChangeTheV1ResponseShape()
    {
        using var factory = new PatchesFactory(patches: true);
        var client = await SeedAsync(factory);

        var body = await (await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(1)))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["confidence", "findings", "image_id", "timestamp", "verdict"],
            body.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Off_LeavesNoScoresAndNeverAsksForPatches()
    {
        using var factory = new PatchesFactory(patches: false);
        var client = await SeedAsync(factory);
        await InspectAsync(client, Jpeg(1));

        var annotations = await LastAnnotationsAsync(factory);
        Assert.DoesNotContain("memory:patch_score", annotations.Keys);
        Assert.DoesNotContain("memory:global_score", annotations.Keys);
        Assert.Equal(0, factory.Embedder.PatchCalls);
    }

    [Fact]
    public async Task Sync_CachesPatchesOnDisk_AndDoesNotReembedWhenNothingChanged()
    {
        using var factory = new PatchesFactory(patches: true);
        await SeedAsync(factory);
        var memory = factory.Services.GetRequiredService<CaseMemory>();
        var callsAfterFirst = factory.Embedder.PatchCalls;
        Assert.Equal(2, factory.Embedder.PatchedImages);   // 정상 둘만 — 불량은 패치 기억에 안 든다

        await memory.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(callsAfterFirst, factory.Embedder.PatchCalls);

        var dir = Path.Combine(factory.DataPath, "demo", "memory", MockImageEmbedder.Identity.Key, "patches");
        Assert.Equal(2, Directory.GetFiles(dir, "*.f32").Length);
    }

    [Fact]
    public async Task PatchBankCap_KeepsOnlyTheMostRecentNormalCases()
    {
        using var factory = new PatchesFactory(patches: true, cap: 1);
        var client = factory.CreateClient();
        await LabelAsync(client, await InspectAsync(client, Jpeg(1)), "OK");
        await LabelAsync(client, await InspectAsync(client, Jpeg(2)), "OK");
        factory.Embedder.Reset();
        await factory.Services.GetRequiredService<CaseMemory>().SyncOnceAsync(CancellationToken.None);
        Assert.Equal(1, factory.Embedder.PatchedImages);   // 동기화는 상한 안의 한 장만 임베딩한다

        await InspectAsync(client, Jpeg(1));
        Assert.Equal("1", (await LastAnnotationsAsync(factory))["memory:bank_size"]);
    }

    [Fact]
    public async Task PatchBudget_ReducesThePatchPoolButNotThePhotosOrTheGlobalScore_AndTheLedgerSaysSo()
    {
        // mock 은 사진 한 장이 4 패치 — 정상 둘이면 풀이 8 패치. 상한 4 로 줄인다.
        using var reduced = new PatchesFactory(patches: true, patchBudget: 4);
        using var full = new PatchesFactory(patches: true);
        var reducedClient = await SeedAsync(reduced);
        var fullClient = await SeedAsync(full);

        await InspectAsync(reducedClient, Jpeg(9));
        await InspectAsync(fullClient, Jpeg(9));
        var annotations = await LastAnnotationsAsync(reduced);
        var baseline = await LastAnnotationsAsync(full);

        Assert.Equal("4", annotations["memory:bank_patches"]);
        Assert.Equal("2", annotations["memory:bank_size"]);          // 사진 수는 그대로
        Assert.False(baseline.ContainsKey("memory:bank_patches"));   // 안 줄였으면 키가 없다(꺼짐 = 지금 동작)
        Assert.Equal(Number(baseline, "memory:global_score"), Number(annotations, "memory:global_score"), 6);   // 전역 점수는 사진 전부 위에서
        Assert.True(Number(annotations, "memory:patch_score") >= Number(baseline, "memory:patch_score") - 1e-9);   // 기억이 줄면 거리는 줄지 않는다
    }

    /// <summary>시나리오 «demo» 의 패치 인코더를 정하고(없으면 지움) 정상 둘 · 불량 하나를 확정한 기억을 짓는다.</summary>
    private static async Task<(HttpClient Client, CaseMemory Memory)> SeedWithPatchEncoderAsync(PatchesFactory factory, string? patchEncoder)
    {
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<UVision.Api.Storage.IScenarioStore>();
        if (await store.GetAsync("demo") is null)
            await client.PostAsync("/api/u-vision/inspect", Form(Jpeg(1)));   // 기본 시나리오가 파일로 생기게
        var demo = (await store.GetAsync("demo"))!;
        if (patchEncoder is not null)
            await store.UpdateAsync("demo", new ScenarioInput { Name = demo.Name, PatchEncoder = patchEncoder });
        await LabelAsync(client, await InspectAsync(client, Jpeg(1)), "OK");
        await LabelAsync(client, await InspectAsync(client, Jpeg(2)), "OK");
        await LabelAsync(client, await InspectAsync(client, Jpeg(3)), "NG");
        var memory = factory.Services.GetRequiredService<CaseMemory>();
        await memory.SyncOnceAsync(CancellationToken.None);
        await memory.SyncOnceAsync(CancellationToken.None);   // 패치 인코더의 판을 배운 뒤 한 번 더
        return (client, memory);
    }

    [Fact]
    public async Task ScenarioPatchEncoder_MovesOnlyThePatchesToThatEncoder_TheGlobalMemoryStaysOnTheInstalledOne()
    {
        using var chosen = new PatchesFactory(patches: true);
        using var installed = new PatchesFactory(patches: true);
        var (chosenClient, chosenMemory) = await SeedWithPatchEncoderAsync(chosen, "dinov2-small-mid6");
        var (installedClient, _) = await SeedWithPatchEncoderAsync(installed, null);
        chosen.Embedder.Encoders.Clear();
        var globalBefore = chosen.Embedder.GlobalCalls;

        await InspectAsync(chosenClient, Jpeg(9));
        await InspectAsync(installedClient, Jpeg(9));
        var a = await LastAnnotationsAsync(chosen);
        var b = await LastAnnotationsAsync(installed);

        // 패치 호출은 그 시나리오가 고른 인코더로, 전역 호출은 인코더 id 없이(설치 인코더) 따로 간다.
        Assert.Equal(["dinov2-small-mid6"], chosen.Embedder.Encoders.Distinct());
        Assert.True(chosen.Embedder.GlobalCalls > globalBefore);
        // 전역 기억은 설치 인코더의 공간에 그대로 선다 — 판 키 · 전역 점수가 안 고른 설치와 같다.
        Assert.Equal(MockImageEmbedder.Identity.Key, chosenMemory.StatusOf("demo")!.Value.EncoderKey);
        Assert.Equal(b["memory:encoder"], a["memory:encoder"]);
        Assert.Equal(Number(b, "memory:global_score"), Number(a, "memory:global_score"), 6);
        // 패치 점수는 고른 인코더의 공간에서 나오고, 그렇다는 표시가 장부에 남는다(안 골랐으면 키가 없다).
        Assert.Equal("dinov2-small-mid6-1", a["memory:patch_encoder"]);
        Assert.False(b.ContainsKey("memory:patch_encoder"));
        Assert.NotEqual(Number(b, "memory:patch_score"), Number(a, "memory:patch_score"));
        // 디스크: 전역 벡터 캐시는 설치 인코더 판 아래, 패치 캐시는 패치 인코더 판 아래.
        var memoryDir = Path.Combine(chosen.DataPath, "demo", "memory");
        Assert.True(File.Exists(Path.Combine(memoryDir, MockImageEmbedder.Identity.Key, "vectors.jsonl")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(memoryDir, "dinov2-small-mid6-1", "patches"), "*.f32"));
    }

    [Fact]
    public async Task ScenarioPatchEncoder_ThatFails_OnlyDropsThePatchScores_NotTheMemoryAnswer()
    {
        using var factory = new PatchesFactory(patches: true);
        factory.Embedder.FailingEncoder = "not-installed";
        var (client, memory) = await SeedWithPatchEncoderAsync(factory, "not-installed");

        await InspectAsync(client, Jpeg(1));   // 확정 사례와 같은 이미지 — 전역 기억은 답한다
        var annotations = await LastAnnotationsAsync(factory);

        Assert.Equal(MockImageEmbedder.Identity.Key, annotations["memory:encoder"]);   // 전역 기억은 살아 있다
        Assert.True(annotations.ContainsKey("memory:nearest_distance"));
        Assert.False(annotations.ContainsKey("memory:patch_score"));                    // 패치 점수만 빠진다
        Assert.NotNull(memory.StatusOf("demo"));
    }

    [Fact]
    public async Task PatchBudget_AtOrAboveThePool_ChangesNothing()
    {
        using var factory = new PatchesFactory(patches: true, patchBudget: 8);   // 풀이 정확히 8 패치
        var client = await SeedAsync(factory);
        await InspectAsync(client, Jpeg(9));
        Assert.False((await LastAnnotationsAsync(factory)).ContainsKey("memory:bank_patches"));
    }

    [Fact]
    public void ReducePatches_KeepsRowsFromTheInput_AndIsDeterministic()
    {
        const int count = 40, dim = 4, keep = 10;
        var rng = new Random(7);
        var vectors = Enumerable.Range(0, count * dim).Select(_ => (float)rng.NextDouble()).ToArray();

        var once = CaseMemory.ReducePatches(vectors, count, dim, keep);
        var twice = CaseMemory.ReducePatches(vectors, count, dim, keep);

        Assert.Equal(keep * dim, once.Length);
        Assert.Equal(once, twice);
        var inputRows = Enumerable.Range(0, count).Select(i => vectors[(i * dim)..((i + 1) * dim)]).ToList();
        for (var i = 0; i < keep; i++)
            Assert.Contains(inputRows, row => row.SequenceEqual(once[(i * dim)..((i + 1) * dim)]));
    }

    [Fact]
    public async Task PatchFiles_FollowTheBank_NewNormalCasesDoNotPileUpFilesPastTheCap()
    {
        using var factory = new PatchesFactory(patches: true, cap: 1);
        var client = factory.CreateClient();
        var memory = factory.Services.GetRequiredService<CaseMemory>();
        var dir = Path.Combine(factory.DataPath, "demo", "memory", MockImageEmbedder.Identity.Key, "patches");

        await LabelAsync(client, await InspectAsync(client, Jpeg(1)), "OK");
        await memory.SyncOnceAsync(CancellationToken.None);
        Assert.Single(Directory.GetFiles(dir, "*.f32"));

        // 정상이 더 확정되면 기억의 한 자리를 새 사례가 차지한다 — 밀려난 사례의 파일은 남지 않는다.
        await LabelAsync(client, await InspectAsync(client, Jpeg(2)), "OK");
        await LabelAsync(client, await InspectAsync(client, Jpeg(3)), "OK");
        await memory.SyncOnceAsync(CancellationToken.None);
        Assert.Single(Directory.GetFiles(dir, "*.f32"));
    }

    [Fact]
    public async Task On_KeepsAnAnomalyMapPerInspection_ThatAgreesWithTheLedgerScores()
    {
        using var factory = new PatchesFactory(patches: true);
        var client = await SeedAsync(factory);

        var imageId = await InspectAsync(client, Jpeg(9));
        var annotations = await LastAnnotationsAsync(factory);

        var response = await client.GetAsync($"/api/u-vision/results/anomaly-map?scenario_id=demo&date={Today}&image_id={imageId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var map = (await response.Content.ReadFromJsonAsync<JsonElement>());
        var distances = map.GetProperty("distances").EnumerateArray().Select(d => d.GetDouble()).ToList();
        Assert.Equal(map.GetProperty("rows").GetInt32() * map.GetProperty("columns").GetInt32(), distances.Count);
        Assert.Equal(Number(annotations, "memory:patch_score"), map.GetProperty("patch_score").GetDouble(), 5);
        Assert.Equal(Number(annotations, "memory:global_score"), map.GetProperty("global_score").GetDouble(), 5);
        Assert.Equal(distances.Max(), map.GetProperty("patch_score").GetDouble(), 3);   // 지도는 소수 넷째 자리
        Assert.Equal(2, map.GetProperty("bank_size").GetInt32());
        Assert.Equal(MockImageEmbedder.Identity.Key, map.GetProperty("encoder").GetString());

        // 지도 사이드카가 결과 목록을 오염시키지 않는다 — 결과는 검사 건수만큼.
        var results = await client.GetFromJsonAsync<JsonElement>($"/api/u-vision/results?scenario_id=demo&date={Today}");
        Assert.Equal(4, results.GetArrayLength());   // 시드 3 + 방금 1
    }

    [Fact]
    public async Task AnomalyMap_Missing_Is404_AndBadIdsAre400_AndOffMeansNoMaps()
    {
        using var factory = new PatchesFactory(patches: false);
        var client = await SeedAsync(factory);
        var imageId = await InspectAsync(client, Jpeg(1));

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/u-vision/results/anomaly-map?scenario_id=demo&date={Today}&image_id={imageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/u-vision/results/anomaly-map?scenario_id=demo&date={Today}&image_id=..%2Fx")).StatusCode);
    }

    [Theory]
    [InlineData("none", 200)]   // 패치 모드가 없는 provider
    [InlineData("mock", 0)]     // 상한 범위 밖
    [InlineData("mock", 5001)]
    public void Factory_RejectsPatchesThatCannotRun(string provider, int cap) =>
        Assert.Throws<ArgumentException>(() => ImageEmbedderFactory.Create(
            new MemoryOptions { Provider = provider, Patches = true, PatchBankCap = cap }));

    [Theory]
    [InlineData(-1)]
    [InlineData(5_000_001)]
    public void Factory_RejectsAPatchBudgetOutOfRange(int budget) =>
        Assert.Throws<ArgumentException>(() => ImageEmbedderFactory.Create(
            new MemoryOptions { Provider = "mock", Patches = true, PatchBankPatches = budget }));

    [Fact]
    public void EncoderKey_IncludesThePreprocessor_SoAChangedPreprocessingIsANewSpace()
    {
        var weights = new EncoderIdentity("dinov2-small", "1", "ABCDEF0123456789ff", 3, true);
        var v2 = weights with { Preprocessor = "pillow/2" };
        var v3 = weights with { Preprocessor = "pillow/3" };

        Assert.Equal("dinov2-small-abcdef012345", weights.Key);
        Assert.Equal("dinov2-small-abcdef012345-pillow_2", v2.Key);
        Assert.NotEqual(v2.Key, v3.Key);
    }
}
