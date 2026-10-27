using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HoneAI;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Services.Vlm;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 결정 장부 — inspect 마다 라우터 provenance 한 줄이 <c>{scenario}/ledger/{date}.jsonl</c> 에 남고,
/// 실제로 쓴 결정이 annotation 으로 나란히 적히며, 장부 실패가 판정을 막지 않는지.
/// </summary>
public class DecisionLedgerTests
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static MultipartFormDataContent Form(string scenarioId = "demo")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(scenarioId), "scenario_id");
        return form;
    }

    private static string LedgerFile(UVisionApiFactory factory) => Path.Combine(
        factory.DataPath, "demo", "ledger",
        DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

    private static async Task<List<ProvenanceRecord>> ReadLedgerAsync(UVisionApiFactory factory)
    {
        var records = new List<ProvenanceRecord>();
        await foreach (var r in new JsonlProvenanceSink(LedgerFile(factory)).ReadAsync())
            records.Add(r);
        return records;
    }

    [Fact]
    public async Task VlmOnly_RecordsOneTierLadder_SettledByVlm()
    {
        using var factory = new UVisionApiFactory();
        var resp = await factory.CreateClient().PostAsync("/api/u-vision/inspect", Form());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var record = Assert.Single(await ReadLedgerAsync(factory));
        Assert.Equal(body.GetProperty("image_id").GetString(), record.SubjectId);
        var p = record.Provenance;
        Assert.Equal(JudgeKind.Vlm, p.Kind);
        Assert.Equal("vlm", p.TierId);
        Assert.Equal("demo", p.SliceKey);
        Assert.Equal([("vlm", StepOutcome.Settled)], p.Path!.Select(s => (s.TierId, s.Outcome)));
        Assert.Equal(body.GetProperty("verdict").GetString(), p.Annotations!["operative:verdict"]);
        Assert.Equal("vlm", p.Annotations["operative:source"]);
        Assert.Equal("advisory", p.Annotations["operative:stage"]);
        // 교사 판 — 모델 이름 뒤에 프롬프트 · 기준 이미지 해시가 붙는다(기준 이미지 없는 시나리오는 r:none).
        Assert.Matches(@"^mock:[^|]+\|p:[0-9a-f]{8}\|r:none$", p.TierVersion);
    }

    [Fact]
    public async Task TeacherEdition_FollowsReferenceImages_NotJustTheModel()
    {
        // 같은 모델 · 같은 프롬프트라도 기준 이미지가 바뀌면 교사 판(r)이 바뀌고, 안 바뀌면 같다.
        async Task<string> EditionAfterAsync(string scenarioId, params byte[][] references)
        {
            using var factory = new UVisionApiFactory();
            factory.SeedScenario(new Scenario { ScenarioId = scenarioId, Name = "x" });
            foreach (var r in references)
                factory.SeedReference(scenarioId, ReferenceLabel.Ok, r);
            (await factory.CreateClient().PostAsync("/api/u-vision/inspect", Form(scenarioId))).EnsureSuccessStatusCode();

            var file = Path.Combine(factory.DataPath, scenarioId, "ledger",
                DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            var records = new List<ProvenanceRecord>();
            await foreach (var r in new JsonlProvenanceSink(file).ReadAsync())
                records.Add(r);
            return Assert.Single(records).Provenance.TierVersion!;
        }

        var refA = Jpeg.Concat(new byte[] { 1 }).ToArray();
        var refB = Jpeg.Concat(new byte[] { 2 }).ToArray();
        var withA = await EditionAfterAsync("ed-a", refA);
        Assert.Matches(@"\|r:[0-9a-f]{8}$", withA);
        Assert.Equal(withA, await EditionAfterAsync("ed-a", refA));
        Assert.NotEqual(withA, await EditionAfterAsync("ed-a", refB));
        Assert.EndsWith("|r:none", await EditionAfterAsync("ed-a"));
    }

    [Fact]
    public async Task MlEnabled_MlTierIsHeldBelow_VlmSettles()
    {
        using var factory = new MlEnabledFactory();
        var resp = await factory.CreateClient().PostAsync("/api/u-vision/inspect", Form());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var p = Assert.Single(await ReadLedgerAsync(factory)).Provenance;
        Assert.Equal("vlm", p.TierId);
        Assert.Equal(2, p.Path!.Count);
        Assert.Equal("ml", p.Path[0].TierId);
        // 정책 스냅숏이 없으니 ML 은 확정하지 못한다 — OK 면 Shadow, NG 면 놓치면 안 되는 값이라 위로.
        Assert.Contains(p.Path[0].Outcome, new[] { StepOutcome.Shadowed, StepOutcome.Escalated });
        Assert.Equal(StepOutcome.Settled, p.Path[1].Outcome);
    }

    [Fact]
    public async Task ScenarioJudges_DecideTheLadder_EvenWhenMlIsOn()
    {
        using var factory = new MlEnabledFactory();
        var client = factory.CreateClient(); // 호스트가 뜨며 demo 시나리오를 심는다
        // 시나리오 v2 가 단을 [vlm] 으로 정하면 ML 이 켜져 있어도 장부 사다리에 ML 단이 없다.
        var path = Path.Combine(factory.DataPath, "demo", "scenario.json");
        var scenario = JsonSerializer.Deserialize<Scenario>(await File.ReadAllTextAsync(path), Api.Storage.StoragePaths.Json)!
            with { Judges = [new() { Tier = "vlm" }] };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(scenario, Api.Storage.StoragePaths.Json));

        var resp = await client.PostAsync("/api/u-vision/inspect", Form());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var p = Assert.Single(await ReadLedgerAsync(factory)).Provenance;
        Assert.Equal([("vlm", StepOutcome.Settled)], p.Path!.Select(s => (s.TierId, s.Outcome)));
    }

    [Fact]
    public async Task VlmDown_RecordsUnsettledPath_WithFailClosedPosture()
    {
        using var factory = new MlEnabledFactory();
        var client = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IVlmProvider>(new ThrowingVlmProvider()))).CreateClient();
        var resp = await client.PostAsync("/api/u-vision/inspect", Form());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);

        var p = Assert.Single(await ReadLedgerAsync(factory)).Provenance;
        Assert.Equal(StepOutcome.Abstained, p.Path![^1].Outcome);
        Assert.Equal("none", p.Annotations!["operative:verdict"]);
        Assert.Equal("fail_closed", p.Annotations["operative:posture"]);
    }

    [Fact]
    public async Task LedgerWriteFailure_DoesNotBlockInspect()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient(); // 호스트가 뜨며 demo 시나리오를 심는다
        // 장부 디렉토리 자리에 파일을 두어 쓰기가 실패하게 한다.
        await File.WriteAllTextAsync(Path.Combine(factory.DataPath, "demo", "ledger"), "");

        var resp = await client.PostAsync("/api/u-vision/inspect", Form());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private sealed class ThrowingVlmProvider : IVlmProvider
    {
        public string Name => "throwing-vlm";
        public Task<InspectionResult> InspectAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated VLM endpoint down");
    }
}
