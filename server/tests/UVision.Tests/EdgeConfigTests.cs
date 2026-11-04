using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Auth;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 엣지 구성 폴링(D-102 E-2 · E-3) — 엔진이 정본이고 노드는 읽기 전용 사본을 쥔다. 배정된 시나리오의 투영을 내용 지문(<c>ETag</c>)
/// 조건부로 주고, 응답의 <c>Date</c> 는 에이전트가 시계 오프셋을 재는 재료다.
/// </summary>
public class EdgeConfigTests
{
    private const string Base = "/api/u-vision/edge";

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }

    private sealed class ConfigFactory : UVisionApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton(new AdminPinOptions { Pin = PinFactory.Pin });
                s.AddSingleton<TimeProvider>(new FakeTime(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)));
            });
        }
    }

    private static HttpClient Admin(ConfigFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        return c;
    }

    private static async Task<(HttpClient Node, string NodeId)> NodeAsync(ConfigFactory f, params string[] scenarios)
    {
        var admin = Admin(f);
        var token = (await (await admin.PostAsJsonAsync($"{Base}/tokens", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var reg = await (await f.CreateClient().PostAsJsonAsync($"{Base}/v1/register", new { token })).Content.ReadFromJsonAsync<JsonElement>();
        var nodeId = reg.GetProperty("node_id").GetString()!;
        if (scenarios.Length > 0)
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Base}/nodes/{nodeId}/scenarios", new { scenarios })).StatusCode);
        var node = f.CreateClient();
        node.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{nodeId}.{reg.GetProperty("secret").GetString()}");
        return (node, nodeId);
    }

    private static async Task SeedCountingAsync(HttpClient admin, string id, int expected = 4)
    {
        var resp = await admin.PostAsJsonAsync("/api/u-vision/scenarios", new
        {
            scenario_id = id, name = "재고 수량", criteria = "SECRET PROMPT TEXT", type = "counting",
            expected_count = expected, count_tolerance = 1, min_confidence = 0.6, count_classes = new[] { "helmet" },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task ConfigIsTheProjectionOfTheAssignedScenarios_NotTheWholeScenario()
    {
        using var f = new ConfigFactory();
        await SeedCountingAsync(Admin(f), "stock");
        var (node, nodeId) = await NodeAsync(f, "stock");

        var resp = await node.GetAsync($"{Base}/v1/config");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(nodeId, body.GetProperty("node_id").GetString());
        var s = Assert.Single(body.GetProperty("scenarios").EnumerateArray());
        Assert.Equal("stock", s.GetProperty("scenario_id").GetString());
        Assert.Equal("counting", s.GetProperty("type").GetString());
        Assert.Equal(4, s.GetProperty("expected_count").GetInt32());
        Assert.Equal(1, s.GetProperty("count_tolerance").GetInt32());
        Assert.Equal(0.6, s.GetProperty("min_confidence").GetDouble());
        Assert.Equal(["helmet"], s.GetProperty("count_classes").EnumerateArray().Select(c => c.GetString()));
        // 엣지가 쓰지 않는 것은 보내지 않는다 — VLM 프롬프트 · 클라우드 송출 허용 · 기준 이미지 수.
        var text = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET PROMPT TEXT", text);
        Assert.DoesNotContain("allow_cloud_egress", text);
        Assert.DoesNotContain("criteria", text);
    }

    [Fact]
    public async Task ETagMakesThePollConditional_AndTheDateIsTheClockReference()
    {
        using var f = new ConfigFactory();
        await SeedCountingAsync(Admin(f), "stock");
        var (node, _) = await NodeAsync(f, "stock");

        var first = await node.GetAsync($"{Base}/v1/config");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        Assert.False(etag!.IsWeak);
        // Date 는 서버 시계 — 에이전트가 «서버 시각 − 노드 시각» 을 잴 때 쓴다.
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), first.Headers.Date);
        Assert.Equal(etag.Tag.Trim('"'), (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());

        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/v1/config");
        req.Headers.IfNoneMatch.Add(etag);
        var again = await node.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync());
        // 304 도 Date 를 준다 — 오프셋 재기는 구성이 바뀌지 않아도 계속된다.
        Assert.Equal(first.Headers.Date, again.Headers.Date);
    }

    [Fact]
    public async Task TheVersionChangesWithWhatTheNodeNeeds_AndOnlyWithThat()
    {
        using var f = new ConfigFactory();
        var admin = Admin(f);
        await SeedCountingAsync(admin, "stock");
        var (node, nodeId) = await NodeAsync(f, "stock");
        async Task<string> VersionAsync() =>
            (await (await node.GetAsync($"{Base}/v1/config")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;

        var v1 = await VersionAsync();
        Assert.Equal(v1, await VersionAsync());   // 같은 구성이면 같은 지문

        // 엣지가 쓰는 값이 바뀌면 지문이 바뀐다.
        var put = await admin.PutAsJsonAsync("/api/u-vision/scenarios/stock", new
        {
            name = "재고 수량", criteria = "SECRET PROMPT TEXT", type = "counting", expected_count = 9, count_tolerance = 1,
            min_confidence = 0.6, count_classes = new[] { "helmet" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var v2 = await VersionAsync();
        Assert.NotEqual(v1, v2);

        // 엣지가 쓰지 않는 값(VLM 프롬프트)이 바뀌어도 지문은 그대로다 — 노드를 괜히 깨우지 않는다.
        await admin.PutAsJsonAsync("/api/u-vision/scenarios/stock", new
        {
            name = "재고 수량", criteria = "A DIFFERENT PROMPT", type = "counting", expected_count = 9, count_tolerance = 1,
            min_confidence = 0.6, count_classes = new[] { "helmet" },
        });
        Assert.Equal(v2, await VersionAsync());

        // 배정이 바뀌면 지문이 바뀐다.
        await admin.PutAsJsonAsync($"{Base}/nodes/{nodeId}/scenarios", new { scenarios = Array.Empty<string>() });
        Assert.NotEqual(v2, await VersionAsync());
    }

    [Fact]
    public async Task NoAssignment_IsAnEmptyConfig_AndADeletedScenarioIsNamedNotSilentlyDropped()
    {
        using var f = new ConfigFactory();
        var admin = Admin(f);
        var (node, nodeId) = await NodeAsync(f);
        var empty = await (await node.GetAsync($"{Base}/v1/config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(empty.GetProperty("scenarios").EnumerateArray());
        Assert.False(empty.TryGetProperty("missing_scenarios", out _));

        await SeedCountingAsync(admin, "gone");
        await admin.PutAsJsonAsync($"{Base}/nodes/{nodeId}/scenarios", new { scenarios = new[] { "gone" } });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/u-vision/scenarios/gone")).StatusCode);

        var cfg = await (await node.GetAsync($"{Base}/v1/config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(cfg.GetProperty("scenarios").EnumerateArray());
        Assert.Equal(["gone"], cfg.GetProperty("missing_scenarios").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task ConfigNeedsANodeCredential()
    {
        using var f = new ConfigFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync($"{Base}/v1/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(f).GetAsync($"{Base}/v1/config")).StatusCode);
    }
}
