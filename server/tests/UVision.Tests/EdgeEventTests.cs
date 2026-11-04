using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Auth;
using UVision.Api.OpcUa;
using UVision.Api.Services.Edge;
using UVision.Api.Services.Events;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 엣지 사건 수신(D-102 E-4 · E-5) — 항목별 결과, <c>(node_id, event_id)</c> 중복 제거, <c>seq</c> 구멍 감지, 시각 이중 기록, 배정된
/// 시나리오에만, 기존 사건 내보내기 outbox 로 같은 봉투 전달, 오래된 표지 정리.
/// </summary>
public class EdgeEventTests
{
    private const string Base = "/api/u-vision/edge";

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>PIN + 고정 시계 + 웹훅 하나(받는 곳이 있어야 outbox 가 쓰인다 — 도달할 수 없는 주소라 보내기는 실패하고 파일은 남는다).</summary>
    private sealed class EdgeFactory : UVisionApiFactory
    {
        public readonly FakeTime Time = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"{UVisionOptions.SectionName}:Events:Webhooks:0:Name", "mes");
            builder.UseSetting($"{UVisionOptions.SectionName}:Events:Webhooks:0:Url", "http://127.0.0.1:1/hook");
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton(new AdminPinOptions { Pin = PinFactory.Pin });
                s.AddSingleton<TimeProvider>(Time);
            });
        }
    }

    private static HttpClient Admin(EdgeFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        return c;
    }

    /// <summary>노드를 등록하고 시나리오 <c>demo</c> 를 배정한 Bearer 클라이언트와 노드 id.</summary>
    private static async Task<(HttpClient Node, string NodeId)> NodeAsync(EdgeFactory f, params string[] scenarios)
    {
        var admin = Admin(f);
        var token = (await (await admin.PostAsJsonAsync($"{Base}/tokens", new { })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var reg = await (await f.CreateClient().PostAsJsonAsync($"{Base}/v1/register", new { token })).Content.ReadFromJsonAsync<JsonElement>();
        var nodeId = reg.GetProperty("node_id").GetString()!;
        var assigned = await admin.PutAsJsonAsync($"{Base}/nodes/{nodeId}/scenarios", new { scenarios = scenarios.Length == 0 ? new[] { "demo" } : scenarios });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var node = f.CreateClient();
        node.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{nodeId}.{reg.GetProperty("secret").GetString()}");
        return (node, nodeId);
    }

    /// <summary>UUIDv7 모양의 사건 id — 앞 48비트는 번호, 버전 7 · 변형 8.</summary>
    private static string Id(int n) => $"018f0000-0000-7000-8000-{n:x12}";

    private static object Evt(int n, long? seq = null, string scenario = "demo", string kind = "count_changed",
        string? time = "2026-11-04T11:59:58+00:00", object? data = null) =>
        new { event_id = Id(n), seq = seq ?? n, scenario_id = scenario, kind, device_time = time, data = data ?? new { count = n } };

    private static async Task<JsonElement> PostAsync(HttpClient node, long? offsetMs, params object[] events)
    {
        var resp = await node.PostAsJsonAsync($"{Base}/v1/events", new { events, clock_offset_ms = offsetMs });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string> Statuses(JsonElement r) =>
        [.. r.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("status").GetString()!)];

    private static string[] EventLines(EdgeFactory f, string nodeId) =>
        Directory.GetFiles(Path.Combine(f.DataPath, "edge", "nodes", nodeId, "events"), "*.jsonl")
            .SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToArray();

    [Fact]
    public async Task AcceptedBatch_IsStoredWithBothTimes_AndForwardedInTheSameEnvelope()
    {
        using var f = new EdgeFactory();
        var (node, nodeId) = await NodeAsync(f);

        var result = await PostAsync(node, offsetMs: 2000, Evt(1), Evt(2));
        Assert.Equal(["accepted", "accepted"], Statuses(result));
        Assert.False(result.TryGetProperty("missing_seq", out _));

        // 장부: 원본 device_time · 서버 received_at · 오프셋 · 파생 corrected_time 이 모두 있고 날짜 폴더는 받은 날이다.
        var lines = EventLines(f, nodeId);
        Assert.Equal(2, lines.Length);
        var row = JsonSerializer.Deserialize<JsonElement>(lines[0]);
        Assert.Equal(nodeId, row.GetProperty("node_id").GetString());
        Assert.Equal(Id(1), row.GetProperty("event_id").GetString());
        Assert.Equal("2026-11-04T11:59:58.0000000Z", row.GetProperty("device_time").GetString());
        Assert.Equal("2026-11-04T12:00:00.0000000+00:00", row.GetProperty("received_at").GetString());
        Assert.Equal(2000, row.GetProperty("clock_offset_ms").GetInt64());
        Assert.Equal("2026-11-04T12:00:00.0000000Z", row.GetProperty("corrected_time").GetString());
        Assert.Equal(1, row.GetProperty("data").GetProperty("count").GetInt32());
        Assert.True(File.Exists(Path.Combine(f.DataPath, "edge", "nodes", nodeId, "events", "2026-11-04.jsonl")));

        // 내보내기: 기존 outbox 에 CloudEvents 봉투(새 type)로 — 보정 시각이 time 이다.
        var outbox = Directory.GetFiles(Path.Combine(f.DataPath, "demo", "events", "mes"), "*.json");
        Assert.Equal(2, outbox.Length);
        var entry = JsonSerializer.Deserialize<OutboxEntry>(File.ReadAllText(outbox.OrderBy(x => x).First()))!;
        var envelope = JsonSerializer.Deserialize<JsonElement>(entry.Body);
        Assert.Equal("1.0", envelope.GetProperty("specversion").GetString());
        Assert.Equal("u-vision.edge.event", envelope.GetProperty("type").GetString());
        Assert.Equal(Id(1), envelope.GetProperty("id").GetString());
        Assert.Equal($"/u-vision/edge/nodes/{nodeId}", envelope.GetProperty("source").GetString());
        Assert.Equal("demo", envelope.GetProperty("subject").GetString());
        Assert.Equal("2026-11-04T12:00:00.0000000Z", envelope.GetProperty("time").GetString());
    }

    [Fact]
    public async Task WithoutAnOffset_NothingIsCorrected_AndTimeIsTheDeviceTime()
    {
        using var f = new EdgeFactory();
        var (node, nodeId) = await NodeAsync(f);
        await PostAsync(node, offsetMs: null, Evt(1));

        var row = JsonSerializer.Deserialize<JsonElement>(EventLines(f, nodeId).Single());
        Assert.False(row.TryGetProperty("clock_offset_ms", out _));
        Assert.False(row.TryGetProperty("corrected_time", out _));
        var entry = JsonSerializer.Deserialize<OutboxEntry>(File.ReadAllText(Directory.GetFiles(Path.Combine(f.DataPath, "demo", "events", "mes"), "*.json").Single()))!;
        Assert.Equal("2026-11-04T11:59:58.0000000Z", JsonSerializer.Deserialize<JsonElement>(entry.Body).GetProperty("time").GetString());
    }

    [Fact]
    public async Task ResendingTheSameEvent_IsOneRecordAndADuplicate_EvenForAFreshStore()
    {
        using var f = new EdgeFactory();
        var (node, nodeId) = await NodeAsync(f);

        Assert.Equal(["accepted"], Statuses(await PostAsync(node, null, Evt(1))));
        // 다음 배치의 같은 id · 같은 배치 안의 되풀이 모두 duplicate.
        Assert.Equal(["duplicate", "accepted", "duplicate"], Statuses(await PostAsync(node, null, Evt(1), Evt(2), Evt(2))));
        Assert.Equal(2, EventLines(f, nodeId).Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(f.DataPath, "demo", "events", "mes"), "*.json").Length);

        // 엔진 재시작과 같은 조건: 메모리에 아무것도 없는 새 저장소 — 표지는 디스크에 있다.
        var sp = f.Services;
        var fresh = new EdgeEventStore(
            sp.GetRequiredService<UVision.Api.Storage.StoragePaths>(), sp.GetRequiredService<EdgeNodeStore>(),
            sp.GetRequiredService<UVision.Api.Storage.IScenarioStore>(), sp.GetRequiredService<DecisionEventPublisher>(),
            f.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<EdgeEventStore>.Instance);
        var nodeRecord = (await sp.GetRequiredService<EdgeNodeStore>().ListAsync(CancellationToken.None)).Single(n => n.NodeId == nodeId);
        var again = await fresh.IngestAsync(nodeRecord, new EdgeEventsRequest { Events = [new EdgeEventInput
        {
            EventId = Id(1), Seq = 1, ScenarioId = "demo", Kind = "count_changed", DeviceTime = "2026-11-04T11:59:58+00:00",
        }] }, CancellationToken.None);
        Assert.Equal("duplicate", again.Results.Single().Status);
        Assert.Equal(2, EventLines(f, nodeId).Length);
    }

    [Fact]
    public async Task EachBadItem_IsRejectedWithItsCode_AndTheRestOfTheBatchStillGoesThrough()
    {
        using var f = new EdgeFactory();
        var (node, nodeId) = await NodeAsync(f);

        var result = await PostAsync(node, null,
            Evt(1),                                                           // 정상
            Evt(2, scenario: "other"),                                        // 배정 안 됨(시나리오는 있음)
            Evt(3, scenario: "ghost-scenario"),                               // 배정 안 됨 — 먼저 걸리는 코드
            new { event_id = "not-a-uuid", seq = 4, scenario_id = "demo", kind = "x", device_time = "2026-11-04T11:00:00Z" },
            new { event_id = "018f0000-0000-4000-8000-000000000005", seq = 5, scenario_id = "demo", kind = "x", device_time = "2026-11-04T11:00:00Z" }, // v4
            Evt(6, seq: -1),                                                  // 음수 seq
            Evt(7, kind: "Bad Kind!"),                                        // 종류
            Evt(8, time: "2026-11-04T11:00:00"),                              // 시간대 없음
            Evt(9, time: null),                                               // 시각 없음
            Evt(10, data: new[] { 1, 2 }),                                    // 객체가 아님
            Evt(11, data: new { blob = new string('x', 17_000) }),            // 16 KiB 초과
            Evt(12, scenario: "edge"));                                       // 예약된 id

        var codes = result.GetProperty("results").EnumerateArray()
            .Select(r => r.TryGetProperty("code", out var c) ? c.GetString() : r.GetProperty("status").GetString()).ToList();
        Assert.Equal(
            ["accepted", "scenario_not_assigned", "scenario_not_assigned", "invalid_event_id", "invalid_event_id", "invalid_seq",
             "invalid_kind", "invalid_time", "invalid_time", "invalid_data", "invalid_data", "invalid_scenario"],
            codes);
        Assert.Single(EventLines(f, nodeId));
    }

    [Fact]
    public async Task AnAssignedButDeletedScenario_IsScenarioUnknown()
    {
        using var f = new EdgeFactory();
        var (node, _) = await NodeAsync(f);
        // 배정은 시나리오가 있을 때만 되므로, 배정 뒤에 시나리오를 지워 «배정됐지만 없는» 상태를 만든다.
        var admin = Admin(f);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/u-vision/scenarios",
            new { scenario_id = "temp-line", name = "t", criteria = "t" })).StatusCode);
        var nodeId = (await node.GetFromJsonAsync<JsonElement>($"{Base}/v1/whoami")).GetProperty("node_id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Base}/nodes/{nodeId}/scenarios", new { scenarios = new[] { "demo", "temp-line" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/u-vision/scenarios/temp-line")).StatusCode);

        var result = await PostAsync(node, null, Evt(1, scenario: "temp-line"));
        Assert.Equal("scenario_unknown", result.GetProperty("results")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task SeqHoles_AreReported_AndFilledByLateArrivals()
    {
        using var f = new EdgeFactory();
        var (node, _) = await NodeAsync(f);

        var first = await PostAsync(node, null, Evt(0), Evt(1), Evt(2), Evt(5), Evt(6));
        var holes = first.GetProperty("missing_seq").EnumerateArray().Select(h => (h.GetProperty("from").GetInt64(), h.GetProperty("to").GetInt64())).ToList();
        Assert.Equal([(3L, 4L)], holes);

        // 늦게 온 3 — 구멍이 줄어든다(순서에 의존하지 않는다).
        var second = await PostAsync(node, null, Evt(3));
        Assert.Equal([(4L, 4L)], second.GetProperty("missing_seq").EnumerateArray().Select(h => (h.GetProperty("from").GetInt64(), h.GetProperty("to").GetInt64())));
        // 4 가 오면 구멍이 없다.
        var third = await PostAsync(node, null, Evt(4));
        Assert.False(third.TryGetProperty("missing_seq", out _));
    }

    [Fact]
    public async Task SeqRangeSet_MergesNeighbours()
    {
        var ranges = new List<long[]>();
        foreach (var n in new long[] { 5, 3, 4, 1, 2, 7 }) EdgeEventStore.Add(ranges, n);
        Assert.Equal([[1L, 5L], [7L, 7L]], ranges);
        Assert.False(EdgeEventStore.Add(ranges, 3));          // 이미 있는 번호
        Assert.Equal([new SeqRange { From = 6, To = 6 }], EdgeEventStore.Holes(ranges));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheClockOffset_IsShownOnTheNode_AndSkewIsOnlyAMark()
    {
        using var f = new EdgeFactory();
        var (node, nodeId) = await NodeAsync(f);
        var admin = Admin(f);

        async Task<JsonElement> NodeRowAsync() =>
            (await admin.GetFromJsonAsync<JsonElement>($"{Base}/nodes")).EnumerateArray().Single(n => n.GetProperty("node_id").GetString() == nodeId);

        Assert.False((await NodeRowAsync()).TryGetProperty("clock_offset_ms", out _));
        await PostAsync(node, 500, Evt(1));
        var small = await NodeRowAsync();
        Assert.Equal(500, small.GetProperty("clock_offset_ms").GetInt64());
        Assert.False(small.GetProperty("clock_skewed").GetBoolean());

        // 임계(60 초)를 넘으면 표시만 한다 — 사건은 그대로 받는다.
        var skewed = await PostAsync(node, 120_000, Evt(2));
        Assert.Equal(["accepted"], Statuses(skewed));
        Assert.True((await NodeRowAsync()).GetProperty("clock_skewed").GetBoolean());
    }

    [Fact]
    public async Task BadBatches_AuthAndRevocation_AreRefused()
    {
        using var f = new EdgeFactory();
        var (node, nodeId) = await NodeAsync(f);

        Assert.Equal(HttpStatusCode.BadRequest, (await node.PostAsJsonAsync($"{Base}/v1/events", new { events = Array.Empty<object>() })).StatusCode);
        var tooMany = Enumerable.Range(0, 501).Select(i => Evt(i)).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await node.PostAsJsonAsync($"{Base}/v1/events", new { events = tooMany })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await node.PostAsJsonAsync($"{Base}/v1/events", new { events = new[] { Evt(1) }, clock_offset_ms = long.MaxValue })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PostAsJsonAsync($"{Base}/v1/events", new { events = new[] { Evt(1) } })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Admin(f).DeleteAsync($"{Base}/nodes/{nodeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await node.PostAsJsonAsync($"{Base}/v1/events", new { events = new[] { Evt(1) } })).StatusCode);
    }

    [Fact]
    public async Task Assignment_NeedsThePin_AndOnlyRealScenarios_AndIsAReplacement()
    {
        using var f = new EdgeFactory();
        var admin = Admin(f);
        var token = (await (await admin.PostAsJsonAsync($"{Base}/tokens", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var nodeId = (await (await f.CreateClient().PostAsJsonAsync($"{Base}/v1/register", new { token })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("node_id").GetString();
        var url = $"{Base}/nodes/{nodeId}/scenarios";

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PutAsJsonAsync(url, new { scenarios = new[] { "demo" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(url, new { scenarios = new[] { "ghost-scenario" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{Base}/nodes/node_ffffffffffffffff/scenarios", new { scenarios = new[] { "demo" } })).StatusCode);

        var ok = await admin.PutAsJsonAsync(url, new { scenarios = new[] { "demo", "demo" } });
        Assert.Equal(["demo"], (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenarios").EnumerateArray().Select(s => s.GetString()));
        var cleared = await admin.PutAsJsonAsync(url, new { scenarios = Array.Empty<string>() });
        Assert.Empty((await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenarios").EnumerateArray());
    }

    [Fact]
    public async Task OldSeenMarks_AreSweptLazily_AndRecentOnesStay()
    {
        using var f = new EdgeFactory();
        var (node, nodeId) = await NodeAsync(f);
        await PostAsync(node, null, Evt(1));
        var old = Path.Combine(f.DataPath, "edge", "nodes", nodeId, "seen", Id(1)[..2], Id(1) + ".json");
        Assert.True(File.Exists(old));

        // 표지는 30일 뒤에 낡고, 정리는 요청 경로에서 최대 한 시간에 한 번 일어난다.
        f.Time.Advance(EdgeEventStore.SeenRetention + TimeSpan.FromDays(1));
        File.SetLastWriteTimeUtc(old, f.Time.GetUtcNow().UtcDateTime - EdgeEventStore.SeenRetention - TimeSpan.FromDays(1));
        await PostAsync(node, null, Evt(2));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(f.DataPath, "edge", "nodes", nodeId, "seen", Id(2)[..2], Id(2) + ".json")));
    }

    [Fact]
    public void TheOpcUaMapper_RecognisesEdgeEnvelopes_AndLeavesDecisionsAlone()
    {
        var edge = """{"specversion":"1.0","id":"x","type":"u-vision.edge.event","data":{}}""";
        var decision = """{"specversion":"1.0","id":"x","type":"u-vision.inspection.decided","data":{}}""";
        Assert.True(ResultMapper.IsEdgeEvent(edge));
        Assert.False(ResultMapper.IsEdgeEvent(decision));
        Assert.False(ResultMapper.IsEdgeEvent("{ not json"));
        Assert.False(ResultMapper.IsEdgeEvent("[1]"));
    }
}
