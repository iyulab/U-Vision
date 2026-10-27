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

        public Task<ImageEmbeddings> EmbedAsync(IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default) =>
            inner.EmbedAsync(images, cancellationToken);

        public Task<PatchEmbeddings> EmbedPatchesAsync(IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default)
        {
            PatchCalls++;
            PatchedImages += images.Count;
            return inner.EmbedPatchesAsync(images, cancellationToken);
        }
    }

    private sealed class PatchesFactory(bool patches, int cap = 200) : UVisionApiFactory
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

    [Theory]
    [InlineData("none", 200)]   // 패치 모드가 없는 provider
    [InlineData("mock", 0)]     // 상한 범위 밖
    [InlineData("mock", 5001)]
    public void Factory_RejectsPatchesThatCannotRun(string provider, int cap) =>
        Assert.Throws<ArgumentException>(() => ImageEmbedderFactory.Create(
            new MemoryOptions { Provider = provider, Patches = true, PatchBankCap = cap }));

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
