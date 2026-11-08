using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Auth;
using UVision.Api.Services.Edge;
using UVision.Api.Services.Events;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 관리자가 노드의 사건을 읽는 면 — 최근 사건(최신 먼저)과 거기서 파생한 «지금 상태». 파생은 도착 순서가 아니라 노드의 <c>seq</c> 로 «마지막» 을 정한다.
/// </summary>
public class EdgeEventReaderTests
{
    private const string Base = "/api/u-vision/edge";

    private sealed class ReaderFactory : UVisionApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton(new AdminPinOptions { Pin = PinFactory.Pin }));
        }
    }

    private static HttpClient Admin(ReaderFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        return c;
    }

    private static async Task<(HttpClient Node, string NodeId)> NodeAsync(ReaderFactory f)
    {
        var admin = Admin(f);
        var token = (await (await admin.PostAsJsonAsync($"{Base}/tokens", new { })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var reg = await (await f.CreateClient().PostAsJsonAsync($"{Base}/v1/register", new { token })).Content.ReadFromJsonAsync<JsonElement>();
        var nodeId = reg.GetProperty("node_id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Base}/nodes/{nodeId}/scenarios", new { scenarios = new[] { "demo" } })).StatusCode);
        var node = f.CreateClient();
        node.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{nodeId}.{reg.GetProperty("secret").GetString()}");
        return (node, nodeId);
    }

    private static string Id(int n) => $"018f0000-0000-7000-8000-{n:x12}";

    private static object Evt(int n, string kind, object data, long? seq = null) =>
        new { event_id = Id(n), seq = seq ?? n, scenario_id = "demo", kind, device_time = $"2026-11-04T11:59:{n:00}+00:00", data };

    private static async Task SendAsync(HttpClient node, params object[] events) =>
        Assert.Equal(HttpStatusCode.OK, (await node.PostAsJsonAsync($"{Base}/v1/events", new { events })).StatusCode);

    private static async Task<JsonElement> ReadAsync(HttpClient admin, string nodeId, string query = "")
    {
        var resp = await admin.GetAsync($"{Base}/nodes/{nodeId}/events{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Events_AreNewestFirst_AndCurrentStateIsDerivedFromTheLastOfEachRuleAndSource()
    {
        using var f = new ReaderFactory();
        var (node, nodeId) = await NodeAsync(f);
        await SendAsync(node,
            Evt(1, "count_changed", new { rule = "stock", source = "dock", zone = "z1", @class = "box", count = 4, previous = (int?)null, since = "2026-11-04T11:59:01+00:00" }),
            Evt(2, "count_changed", new { rule = "stock", source = "dock", zone = "z1", @class = "box", count = 6, previous = 4, since = "2026-11-04T11:59:02+00:00" }),
            Evt(3, "alarm_raised", new { rule = "low", source = "dock", zone = "z1", @class = "box", count = 2, max_count = 2, since = "2026-11-04T11:59:03+00:00" }),
            Evt(4, "source_state", new { source = "dock", camera = "cam-1", state = "no_signal", previous = "ok" }),
            Evt(5, "alarm_cleared", new { rule = "low", source = "dock", zone = "z1", @class = "box", count = 5, since = "2026-11-04T11:59:05+00:00", raised_at = "2026-11-04T11:59:03+00:00" }));

        var view = await ReadAsync(Admin(f), nodeId);

        Assert.Equal(nodeId, view.GetProperty("node_id").GetString());
        var ids = view.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("event_id").GetString()).ToArray();
        Assert.Equal([Id(5), Id(4), Id(3), Id(2), Id(1)], ids);
        Assert.Equal(5, view.GetProperty("scanned").GetInt32());
        Assert.False(view.GetProperty("truncated").GetBoolean());

        var current = view.GetProperty("current");
        var count = Assert.Single(current.GetProperty("counts").EnumerateArray());
        Assert.Equal("stock", count.GetProperty("rule").GetString());
        Assert.Equal(6, count.GetProperty("count").GetInt32());   // 4 가 아니라 마지막으로 안정된 6
        Assert.Equal("z1", count.GetProperty("zone").GetString());
        var alarm = Assert.Single(current.GetProperty("alarms").EnumerateArray());
        Assert.Equal("cleared", alarm.GetProperty("state").GetString());   // 올랐다 내려갔다 — 마지막이 내림
        var source = Assert.Single(current.GetProperty("sources").EnumerateArray());
        Assert.Equal("no_signal", source.GetProperty("state").GetString());
        Assert.Equal("cam-1", source.GetProperty("camera").GetString());
    }

    [Fact]
    public async Task Limit_ShortensTheList_ButNotTheScanBehindTheCurrentState()
    {
        using var f = new ReaderFactory();
        var (node, nodeId) = await NodeAsync(f);
        await SendAsync(node,
            Evt(1, "count_changed", new { rule = "stock", source = "dock", @class = "box", count = 9 }),
            Evt(2, "source_state", new { source = "dock", camera = "cam-1", state = "degraded", previous = "ok" }),
            Evt(3, "source_state", new { source = "dock", camera = "cam-1", state = "ok", previous = "degraded" }));

        var view = await ReadAsync(Admin(f), nodeId, "?limit=1");

        Assert.Single(view.GetProperty("events").EnumerateArray());
        Assert.Equal(3, view.GetProperty("scanned").GetInt32());
        Assert.Equal(9, view.GetProperty("current").GetProperty("counts")[0].GetProperty("count").GetInt32());
        Assert.Equal("ok", view.GetProperty("current").GetProperty("sources")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task TheLastIsTheHighestSeq_NotTheLastToArrive()
    {
        using var f = new ReaderFactory();
        var (node, nodeId) = await NodeAsync(f);
        // seq 2 가 먼저 오고 seq 1(더 오래된 값)이 늦게 온다 — 늦게 온 옛 값이 지금 값을 덮으면 안 된다.
        await SendAsync(node, Evt(2, "count_changed", new { rule = "stock", source = "dock", @class = "box", count = 7 }, seq: 2));
        await SendAsync(node, Evt(1, "count_changed", new { rule = "stock", source = "dock", @class = "box", count = 3 }, seq: 1));

        var view = await ReadAsync(Admin(f), nodeId);

        Assert.Equal(7, view.GetProperty("current").GetProperty("counts")[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task LineEvents_AreListedButNotTreatedAsAZoneCount()
    {
        using var f = new ReaderFactory();
        var (node, nodeId) = await NodeAsync(f);
        await SendAsync(node, Evt(1, "count_changed", new { rule = "gate", source = "dock", line = "l1", @class = "box", direction = "in", track_id = "7", @in = 1, @out = 0 }));

        var view = await ReadAsync(Admin(f), nodeId);

        Assert.Single(view.GetProperty("events").EnumerateArray());
        Assert.Empty(view.GetProperty("current").GetProperty("counts").EnumerateArray());
    }

    [Fact]
    public async Task ANodeWithoutEvents_ReadsAsEmpty()
    {
        using var f = new ReaderFactory();
        var (_, nodeId) = await NodeAsync(f);

        var view = await ReadAsync(Admin(f), nodeId);

        Assert.Empty(view.GetProperty("events").EnumerateArray());
        Assert.Empty(view.GetProperty("current").GetProperty("sources").EnumerateArray());
        Assert.Equal(0, view.GetProperty("scanned").GetInt32());
    }

    [Fact]
    public async Task RequiresThePin_RejectsBadInput_AndNamesAnUnknownNode()
    {
        using var f = new ReaderFactory();
        var (_, nodeId) = await NodeAsync(f);
        var admin = Admin(f);

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync($"{Base}/nodes/{nodeId}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"{Base}/nodes/{nodeId}/events?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"{Base}/nodes/{nodeId}/events?limit=501")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Base}/nodes/node_none/events")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"{Base}/nodes/..%2Fx/events")).StatusCode);
    }

    [Fact]
    public async Task ATruncatedLastLine_IsSkipped_NotFatal()
    {
        using var f = new ReaderFactory();
        var (node, nodeId) = await NodeAsync(f);
        await SendAsync(node, Evt(1, "count_changed", new { rule = "stock", source = "dock", @class = "box", count = 2 }));
        var file = Directory.GetFiles(Path.Combine(f.DataPath, "edge", "nodes", nodeId, "events"), "*.jsonl").Single();
        File.AppendAllText(file, "{\"node_id\":\"" + nodeId + "\",\"event_id\":\"cut");   // 쓰다 멈춘 줄

        var view = await ReadAsync(Admin(f), nodeId);

        Assert.Single(view.GetProperty("events").EnumerateArray());
        Assert.Equal(2, view.GetProperty("current").GetProperty("counts")[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public void Builder_IgnoresRecordsWithoutTheFieldsItNeeds()
    {
        var records = new List<EdgeEventRecord>
        {
            Record(1, "count_changed", """{"count": 3}"""),                    // rule 없음
            Record(2, "count_changed", """{"rule": "r", "count": "x"}"""),     // count 가 숫자 아님
            Record(3, "alarm_raised", """{"count": 1}"""),                     // rule 없음
            Record(4, "source_state", """{"camera": "c"}"""),                  // state 없음
            Record(5, "something_else", """{"rule": "r", "count": 1}"""),
            new() { NodeId = "n", EventId = Id(6), Seq = 6, ScenarioId = "demo", Kind = "count_changed", DeviceTime = "t", ReceivedAt = "t" },   // data 없음
        };

        var state = EdgeCurrentStateBuilder.Build(records);

        Assert.Empty(state.Counts);
        Assert.Empty(state.Alarms);
        Assert.Empty(state.Sources);
    }

    private static EdgeEventRecord Record(int n, string kind, string json) => new()
    {
        NodeId = "n", EventId = Id(n), Seq = n, ScenarioId = "demo", Kind = kind, DeviceTime = "t", ReceivedAt = "t",
        Data = JsonDocument.Parse(json).RootElement.Clone(),
    };
}
