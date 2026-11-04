using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Oracle;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 상위 단 재판정 — 저장된 판정에 고른 판정원을 지금 물어 원 판정과 나란히 놓되, <b>판정 · 메트릭 · 결정 장부를 바꾸지 않고</b>
/// 별도 기록에만 남기는지. 관리자 PIN 필수, 이 설치에서 할 수 없는 요청은 400, 판정원의 실패는 기권.
/// </summary>
public class RejudgeTests
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private sealed class FakeOracle(bool enabled, bool cloud, Verdict answer, bool fail = false) : IOracleProvider
    {
        public string Name => "fake-oracle";
        public bool IsEnabled => enabled;
        public bool IsCloud => cloud;

        public Task<InspectionResult> SecondOpinionAsync(
            ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken cancellationToken = default) =>
            fail
                ? throw new InvalidOperationException("oracle down")
                : Task.FromResult(new InspectionResult { Verdict = answer, Confidence = 0.77, Findings = "stronger look" });
    }

    /// <summary>PIN + ML(mock) + 주입한 오라클을 켠 팩토리.</summary>
    private sealed class RejudgeFactory(IOracleProvider oracle, bool ml = true) : UVisionApiFactory
    {
        public const string Pin = "1234";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (ml)
                builder.UseSetting($"{UVisionOptions.SectionName}:Ml:Provider", "mock");
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton(new AdminPinOptions { Pin = Pin });
                s.AddSingleton(oracle);
            });
        }
    }

    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<(string Id, string Class)> InspectAsync(HttpClient client)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        var body = await (await client.PostAsync("/api/u-vision/v2/inspect", form)).Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("decision_id").GetString()!, body.GetProperty("class").GetString()!);
    }

    private static Task<HttpResponseMessage> RejudgeAsync(
        HttpClient client, string imageId, string[] tiers, string? pin = RejudgeFactory.Pin, string scenario = "demo")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/u-vision/results/rejudge")
        {
            Content = JsonContent.Create(new { scenario_id = scenario, date = Today(), image_id = imageId, tiers }),
        };
        if (pin is not null)
            req.Headers.Add("X-Admin-Pin", pin);
        return client.SendAsync(req);
    }

    [Fact]
    public async Task RequiresAdminPin()
    {
        using var factory = new RejudgeFactory(new FakeOracle(true, false, Verdict.OK));
        var client = factory.CreateClient();
        var (id, _) = await InspectAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RejudgeAsync(client, id, ["vlm"], pin: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RejudgeAsync(client, id, ["vlm"], pin: "0000")).StatusCode);
    }

    [Fact]
    public async Task VlmAndMl_AreAskedAgain_AndSummarised()
    {
        using var factory = new RejudgeFactory(new FakeOracle(false, false, Verdict.OK));
        var client = factory.CreateClient();
        var (id, original) = await InspectAsync(client);

        var resp = await RejudgeAsync(client, id, ["vlm", "ml"]);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, body.GetProperty("image_id").GetString());
        Assert.Equal(original, body.GetProperty("original_class").GetString());
        var opinions = body.GetProperty("opinions").EnumerateArray().ToList();
        Assert.Equal(["vlm", "ml"], opinions.Select(o => o.GetProperty("tier").GetString()));
        Assert.All(opinions, o => Assert.Equal("answered", o.GetProperty("status").GetString()));
        Assert.Equal("vlm", opinions[0].GetProperty("kind").GetString());
        // 같은 입력 · 결정적 mock 이라 VLM 은 원 판정과 같다 — 같은 판인지는 version 으로 본다.
        Assert.False(opinions[0].GetProperty("differs_from_original").GetBoolean());
        Assert.StartsWith("mock:", opinions[0].GetProperty("version").GetString());
        var synthesis = body.GetProperty("synthesis");
        Assert.Equal(2, synthesis.GetProperty("answered").GetInt32());
        Assert.Contains(synthesis.GetProperty("agreement").GetString(), new[] { "unanimous", "split" });
    }

    [Fact]
    public async Task Oracle_DisagreeingAnswer_IsASplit_AndNamesTheDifferingTier()
    {
        // mock VLM 의 원 판정과 반대로 답하는 오라클.
        using var probe = new RejudgeFactory(new FakeOracle(true, false, Verdict.OK));
        var (_, probeOriginal) = await InspectAsync(probe.CreateClient());
        var opposite = probeOriginal == "OK" ? Verdict.NG : Verdict.OK;

        using var factory = new RejudgeFactory(new FakeOracle(true, false, opposite));
        var client = factory.CreateClient();
        var (id, original) = await InspectAsync(client);
        Assert.Equal(probeOriginal, original);

        var body = await (await RejudgeAsync(client, id, ["oracle"])).Content.ReadFromJsonAsync<JsonElement>();

        var opinion = Assert.Single(body.GetProperty("opinions").EnumerateArray());
        Assert.Equal("oracle", opinion.GetProperty("tier").GetString());
        Assert.Equal(opposite.ToString(), opinion.GetProperty("class").GetString());
        Assert.True(opinion.GetProperty("differs_from_original").GetBoolean());
        Assert.Equal("fake-oracle", opinion.GetProperty("version").GetString());
        var synthesis = body.GetProperty("synthesis");
        Assert.Equal("split", synthesis.GetProperty("agreement").GetString());
        Assert.Equal(["oracle"], synthesis.GetProperty("differing_tiers").EnumerateArray().Select(t => t.GetString()));
        // 표: 원 판정 한 표 + 오라클 한 표.
        Assert.Equal(1, synthesis.GetProperty("votes").GetProperty(original).GetInt32());
        Assert.Equal(1, synthesis.GetProperty("votes").GetProperty(opposite.ToString()).GetInt32());
    }

    [Fact]
    public async Task DoesNotChangeTheJudgment_TheLedger_OrTheMetrics()
    {
        using var factory = new RejudgeFactory(new FakeOracle(true, false, Verdict.NG));
        var client = factory.CreateClient();
        var (id, _) = await InspectAsync(client);

        string[] Snapshot() => Directory.GetFiles(factory.DataPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}rejudge{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => f + "|" + File.ReadAllText(f).GetHashCode())
            .ToArray();
        var before = Snapshot();

        Assert.Equal(HttpStatusCode.OK, (await RejudgeAsync(client, id, ["vlm", "ml", "oracle"])).StatusCode);

        // 레코드 · 이미지 · 결정 장부 · 메트릭 — 재판정 기록 밖의 어떤 파일도 바뀌지 않았다.
        Assert.Equal(before, Snapshot());
        // 재판정 자신은 별도 기록 한 줄.
        var log = Path.Combine(factory.DataPath, "demo", "rejudge", Today() + ".jsonl");
        Assert.Single(File.ReadAllLines(log));
    }

    [Fact]
    public async Task CannotBeDone_IsA400WithAReason()
    {
        // 오라클 꺼짐 · 클라우드 송출 불허 · 모르는 단 · 빈 단 목록 · 중복 단.
        using (var off = new RejudgeFactory(new FakeOracle(false, false, Verdict.OK)))
        {
            var c = off.CreateClient();
            var (id, _) = await InspectAsync(c);
            Assert.Equal(HttpStatusCode.BadRequest, (await RejudgeAsync(c, id, ["oracle"])).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await RejudgeAsync(c, id, ["wizard"])).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await RejudgeAsync(c, id, [])).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await RejudgeAsync(c, id, ["vlm", "vlm"])).StatusCode);
        }
        using var cloud = new RejudgeFactory(new FakeOracle(true, true, Verdict.OK));
        var cc = cloud.CreateClient();
        var (cid, _) = await InspectAsync(cc);
        var denied = await RejudgeAsync(cc, cid, ["oracle"]);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("allow_cloud_egress", await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownImage_Is404()
    {
        using var factory = new RejudgeFactory(new FakeOracle(true, false, Verdict.OK));
        var client = factory.CreateClient();
        await InspectAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await RejudgeAsync(client, "img_doesnotexist", ["vlm"])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RejudgeAsync(client, "img_x", ["vlm"], scenario: "nope")).StatusCode);
    }

    [Fact]
    public async Task OracleFailure_IsAnAbstention_NotAServerError()
    {
        using var factory = new RejudgeFactory(new FakeOracle(true, false, Verdict.OK, fail: true));
        var client = factory.CreateClient();
        var (id, _) = await InspectAsync(client);

        var resp = await RejudgeAsync(client, id, ["oracle"]);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var opinion = Assert.Single(body.GetProperty("opinions").EnumerateArray());
        Assert.Equal("abstained", opinion.GetProperty("status").GetString());
        Assert.Contains("oracle down", opinion.GetProperty("reason").GetString());
        Assert.False(opinion.TryGetProperty("class", out _));
        Assert.Equal("none_answered", body.GetProperty("synthesis").GetProperty("agreement").GetString());
    }

    [Fact]
    public async Task DisabledTier_AbstainsWithItsReason()
    {
        using var factory = new RejudgeFactory(new FakeOracle(false, false, Verdict.OK), ml: false);
        var client = factory.CreateClient();
        var (id, _) = await InspectAsync(client);

        var body = await (await RejudgeAsync(client, id, ["ml", "memory"])).Content.ReadFromJsonAsync<JsonElement>();
        var reasons = body.GetProperty("opinions").EnumerateArray().Select(o => o.GetProperty("reason").GetString()).ToList();
        Assert.Equal(["ml disabled", "memory disabled"], reasons);
    }
}
