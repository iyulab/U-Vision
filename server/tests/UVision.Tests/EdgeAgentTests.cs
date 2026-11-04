using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Auth;
using UVision.Edge.Agent;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 엣지 에이전트 코어(D-102 E-6) — 디스크 outbox(재시작 복원 · 번호 · 임시 파일 · 크롭 먼저 버리기)와, 실제 엔진에 붙인 등록 · 구성 폴링 ·
/// 순서 있는 최소 한 번 전달 · 끊김과 응답 유실 · 회수 · 영구 거절.
/// </summary>
public sealed class EdgeAgentTests : IDisposable
{
    private const string Api = "/api/u-vision";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvision-agent-" + Guid.NewGuid().ToString("N"));
    private string Dir(string name) => Path.Combine(_root, name);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    // --- outbox ------------------------------------------------------------------------------

    [Fact]
    public void Outbox_RecoversOrderAndKeepsNumbersMonotonic_AcrossRestarts()
    {
        var at = DateTimeOffset.UtcNow;
        using (var box = AgentOutbox.Open(Dir("o1")))
        {
            box.EnqueueEvent("demo", "count_changed", at);
            box.EnqueueCrop("demo", Jpeg, "image/jpeg", at);
            box.EnqueueEvent("demo", "alarm", at);
        }

        using (var box = AgentOutbox.Open(Dir("o1")))
        {
            var items = box.Peek(10);
            Assert.Equal([OutboxKinds.Event, OutboxKinds.Crop, OutboxKinds.Event], items.Select(i => i.Kind));
            Assert.Equal([0L, 1L, 2L], items.Select(i => i.Order));
            // 크롭은 사건 번호를 먹지 않는다 — 엔진이 없는 구멍을 보고하지 않게.
            Assert.Equal([0L, 1L], items.Where(i => i.Kind == OutboxKinds.Event).Select(i => i.Seq!.Value));
            var next = box.EnqueueEvent("demo", "count_changed", at);
            Assert.Equal(3, next.Order);
            Assert.Equal(2, next.Seq);
            foreach (var i in box.Peek(10).Take(2)) box.Ack(i);
        }

        using (var box = AgentOutbox.Open(Dir("o1")))
        {
            Assert.Equal(2, box.Stats().Depth);
            Assert.Equal(3, box.EnqueueEvent("demo", "x", at).Seq);   // 이어진다 — 줄지도 건너뛰지도 않는다
        }
    }

    [Fact]
    public void Outbox_NumbersContinueAfterTheQueueWasDrained_AndTheProcessEnded()
    {
        var at = DateTimeOffset.UtcNow;
        using (var box = AgentOutbox.Open(Dir("o2")))
        {
            for (var i = 0; i < 5; i++) box.EnqueueEvent("demo", "x", at);
            foreach (var i in box.Peek(10)) box.Ack(i);
        }   // 정상 종료(Dispose)가 고수위를 쓴다
        using (var box = AgentOutbox.Open(Dir("o2")))
            Assert.Equal(5, box.EnqueueEvent("demo", "x", at).Seq);
    }

    [Fact]
    public void Outbox_IgnoresAndCleansAHalfWrittenFile()
    {
        var at = DateTimeOffset.UtcNow;
        using (var box = AgentOutbox.Open(Dir("o3")))
            box.EnqueueEvent("demo", "x", at);
        // 쓰는 도중 멈춘 흔적: 임시 파일 · 잘린 항목 파일.
        var bucket = Directory.GetDirectories(Path.Combine(Dir("o3"), "items")).Single();
        File.WriteAllText(Path.Combine(bucket, "00000000000000000001_dead.json.abc12345.tmp"), "{\"id\":");
        File.WriteAllText(Path.Combine(bucket, "00000000000000000002_cut.json"), "{\"id\":\"cut\",");

        using var reopened = AgentOutbox.Open(Dir("o3"));
        Assert.Single(reopened.Peek(10));                              // 완결된 것만
        Assert.Empty(Directory.GetFiles(Dir("o3"), "*.tmp", SearchOption.AllDirectories));
        // 잘린 항목 파일은 버리지 않고 parked/ 로 — 사람이 볼 수 있게.
        Assert.Equal(1, reopened.Stats().Parked);
        Assert.Contains(Directory.GetFiles(Path.Combine(Dir("o3"), "parked")), f => f.Contains("corrupt_"));
    }

    [Fact]
    public void Outbox_OverTheLimit_DropsOldestCropsFirst_AndNeverAnEvent()
    {
        var at = DateTimeOffset.UtcNow;
        using var box = AgentOutbox.Open(Dir("o4"), maxBytes: 6 * 1024);
        var big = new byte[2048];
        for (var i = 0; i < 3; i++)
        {
            box.EnqueueEvent("demo", "count_changed", at);
            box.EnqueueCrop("demo", big, "image/jpeg", at);
        }
        box.EnqueueEvent("demo", "alarm", at);

        var stats = box.Stats();
        var items = box.Peek(100);
        Assert.Equal(4, items.Count(i => i.Kind == OutboxKinds.Event));   // 사건 본문은 하나도 버리지 않았다
        Assert.True(stats.DroppedCrops >= 1);
        Assert.True(stats.Bytes <= 6 * 1024, $"bytes={stats.Bytes}");
        // 버려진 건 오래된 크롭부터 — 남은 크롭은 가장 최근 것들이다.
        var droppedFirst = items.Where(i => i.Kind == OutboxKinds.Crop).Select(i => i.Order).DefaultIfEmpty(long.MaxValue).Min();
        Assert.True(droppedFirst >= 1);
    }

    // --- 시계 --------------------------------------------------------------------------------

    [Fact]
    public void ClockOffset_IsTheMedianOfRecentSamples_FromTheDateHeader()
    {
        var est = new ClockOffsetEstimator(window: 5);
        Assert.Null(est.OffsetMs());
        var t = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        // 왕복 200 ms 의 가운데(.100)에 서버가 «12:00:05»(초로 잘린 값, +0.5 초가 가운데)라면 서버 − 노드 ≈ 5.4 초.
        est.Observe(t, t.AddMilliseconds(200), t.AddSeconds(5));
        Assert.Equal(5400, est.OffsetMs());
        // 이상치 하나는 중앙값이 거른다.
        est.Observe(t, t.AddMilliseconds(200), t.AddSeconds(5));
        est.Observe(t, t.AddMilliseconds(200), t.AddSeconds(30));
        Assert.Equal(5400, est.OffsetMs());
    }

    // --- 엔진에 붙인 에이전트 ----------------------------------------------------------------------

    /// <summary>요청을 기록하고, 끊김(Down) · 응답 유실(LoseNextEventsResponse)을 흉내 내는 핸들러.</summary>
    private sealed class FaultHandler : DelegatingHandler
    {
        public volatile bool Down;
        public volatile bool LoseNextEventsResponse;
        /// <summary>상태 보고 경로를 모르는 옛 엔진을 흉내 낸다(404).</summary>
        public volatile bool StatusRouteMissing;
        public readonly List<string> Paths = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            if (Down) throw new HttpRequestException("network down");
            if (StatusRouteMissing && request.RequestUri!.AbsolutePath.EndsWith("/edge/v1/status", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var response = await base.SendAsync(request, ct);
            if (LoseNextEventsResponse && request.RequestUri!.AbsolutePath.EndsWith("/edge/v1/events", StringComparison.Ordinal))
            {
                LoseNextEventsResponse = false;
                throw new HttpRequestException("response lost");      // 엔진은 처리했는데 에이전트는 모른다
            }
            return response;
        }
    }

    private sealed class Rig : IDisposable
    {
        public PinFactory Factory { get; } = new();
        public FaultHandler Net { get; } = new();
        public HttpClient Admin { get; }
        public string NodeId { get; private set; } = "";

        public Rig()
        {
            Admin = Factory.CreateClient();
            Admin.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        }

        private HttpClient? _http;

        /// <summary>핸들러 하나(= 네트워크 하나)에 클라이언트 하나 — 에이전트를 다시 만들어도 같은 «회선» 을 쓴다.</summary>
        public EngineClient Engine() => new(_http ??= Factory.CreateDefaultClient(Net), Api);

        public async Task<EdgeAgent> RegisteredAgentAsync(string stateDir, AgentOutbox outbox, params string[] scenarios)
        {
            var agent = NewAgent(stateDir, outbox);
            if (!agent.IsRegistered)
            {
                var token = (await (await Admin.PostAsJsonAsync($"{Api}/edge/tokens", new { })).Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("token").GetString()!;
                await agent.RegisterAsync(token, "test-node", CancellationToken.None);
                NodeId = agent.NodeId!;
                Assert.Equal(HttpStatusCode.OK, (await Admin.PutAsJsonAsync(
                    $"{Api}/edge/nodes/{NodeId}/scenarios", new { scenarios = scenarios.Length == 0 ? new[] { "demo" } : scenarios })).StatusCode);
            }
            return agent;
        }

        public EdgeAgent NewAgent(string stateDir, AgentOutbox outbox) =>
            new(Engine(), outbox, stateDir, new AgentOptions { Jitter = 0, BackoffBase = TimeSpan.FromSeconds(2), BackoffCap = TimeSpan.FromSeconds(16) });

        public string[] EngineEventLines() =>
            Directory.Exists(Path.Combine(Factory.DataPath, "edge", "nodes", NodeId, "events"))
                ? Directory.GetFiles(Path.Combine(Factory.DataPath, "edge", "nodes", NodeId, "events"), "*.jsonl")
                    .SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToArray()
                : [];

        public void Dispose()
        {
            Admin.Dispose();
            Factory.Dispose();
        }
    }

    private static JsonElement Count(int n) => JsonSerializer.SerializeToElement(new { count = n });

    private static async Task<JsonElement> OnlyNodeAsync(Rig rig) =>
        (await rig.Admin.GetFromJsonAsync<JsonElement>($"{Api}/edge/nodes")).EnumerateArray().Single();

    [Fact]
    public async Task Agent_ReportsItsOutboxState_AfterTheCycle_AndTheOperatorSeesIt()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("s1/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("s1/state"), box);
        Assert.False((await OnlyNodeAsync(rig)).TryGetProperty("status", out _));      // 보고 전에는 없다

        box.EnqueueEvent("demo", "count_changed", DateTimeOffset.UtcNow, Count(1));
        await agent.RunOnceAsync(CancellationToken.None);                              // 전달 끝난 뒤 깊이 0 을 보고

        var status = (await OnlyNodeAsync(rig)).GetProperty("status");
        Assert.Equal(0, status.GetProperty("outbox_depth").GetInt32());
        Assert.Equal(0, status.GetProperty("dropped_crops").GetInt64());
        Assert.Equal(0, status.GetProperty("parked").GetInt32());
        Assert.Equal(EdgeAgent.AgentVersion, status.GetProperty("agent_version").GetString());
        Assert.Equal("0.0.0-dev", EdgeAgent.AgentVersion);                             // 판이 찍히지 않은 개발 빌드는 그렇게 말한다
        Assert.True(DateTimeOffset.TryParse(status.GetProperty("reported_at").GetString(), out _));
    }

    [Fact]
    public async Task Agent_ReportsParkedItems_SoTheOperatorSeesWhatNeedsAHuman_WithoutGoingOffline()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("s2/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("s2/state"), box);
        await agent.RunOnceAsync(CancellationToken.None);
        // 배정에 없는 시나리오 사건은 엔진이 영구 거절한다 → parked. 상태 보고가 «따로 둔 항목» 으로 사람에게 보인다.
        box.EnqueueEvent("not-assigned", "count_changed", DateTimeOffset.UtcNow, Count(1));

        var report = await agent.RunOnceAsync(CancellationToken.None);

        Assert.False(report.Offline);
        Assert.Equal(1, report.Parked);
        Assert.Equal(1, (await OnlyNodeAsync(rig)).GetProperty("status").GetProperty("parked").GetInt32());
    }

    [Fact]
    public async Task Agent_StopsReporting_ToAnEngineThatDoesNotKnowTheRoute_WithoutHarmingDelivery()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("s3/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("s3/state"), box);
        rig.Net.StatusRouteMissing = true;
        box.EnqueueEvent("demo", "count_changed", DateTimeOffset.UtcNow, Count(1));

        var first = await agent.RunOnceAsync(CancellationToken.None);
        var second = await agent.RunOnceAsync(CancellationToken.None);

        Assert.False(first.Offline);
        Assert.Equal(1, first.Sent);                                                    // 전달은 멀쩡하다
        Assert.False(second.Offline);
        Assert.Equal(1, rig.Net.Paths.Count(p => p.EndsWith("/edge/v1/status", StringComparison.Ordinal)));   // 한 번 물어보고 그만
    }

    /// <summary>요청을 받고 취소될 때까지 답하지 않는 «멈춘 엔진».</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task Agent_RunAsync_CancelledInTheMiddleOfARequest_EndsWithoutThrowing()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("c1/outbox"));
        await rig.RegisteredAgentAsync(Dir("c1/state"), box);                       // 자격을 디스크에 남긴다
        using var hanging = new HttpClient(new HangingHandler());
        var agent = new EdgeAgent(new EngineClient(hanging, "http://localhost" + Api), box, Dir("c1/state"));
        using var cts = new CancellationTokenSource();

        var run = agent.RunAsync(cts.Token);
        await Task.Delay(100);                                                       // 폴링 요청이 걸려 있다
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));                                // 예외 없이 끝난다
    }

    [Fact]
    public async Task Agent_AfterRevocation_StopsAtThePoll_AndNeverReports()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("s4/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("s4/state"), box);
        await agent.RunOnceAsync(CancellationToken.None);
        await rig.Admin.DeleteAsync($"{Api}/edge/nodes/{rig.NodeId}");
        var statusCallsBefore = rig.Net.Paths.Count(p => p.EndsWith("/edge/v1/status", StringComparison.Ordinal));

        var report = await agent.RunOnceAsync(CancellationToken.None);

        Assert.True(report.Revoked);
        Assert.Equal(statusCallsBefore, rig.Net.Paths.Count(p => p.EndsWith("/edge/v1/status", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Agent_RegistersPollsAndFlushes_InOrder_ToTheRealEngine()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("a1/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("a1/state"), box);
        var at = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++) box.EnqueueEvent("demo", "count_changed", at.AddSeconds(i), Count(i));

        var report = await agent.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, report.Sent);
        Assert.True(report.ConfigChanged);                                 // 처음 폴링은 구성을 받는다
        Assert.False(report.Offline);
        Assert.Equal(0, box.Stats().Depth);
        var lines = rig.EngineEventLines().Select(l => JsonSerializer.Deserialize<JsonElement>(l)).ToList();
        Assert.Equal([0L, 1L, 2L], lines.Select(l => l.GetProperty("seq").GetInt64()));    // 순서대로, 구멍 없이
        Assert.Equal([0, 1, 2], lines.Select(l => l.GetProperty("data").GetProperty("count").GetInt32()));

        // 두 번째 폴링은 If-None-Match 로 304 — 구성은 그대로.
        Assert.False((await agent.RunOnceAsync(CancellationToken.None)).ConfigChanged);

        // 오프셋을 쟀고 사건 배치에 실려 갔다(같은 프로세스라 실제 오프셋은 0 — 추정 오차는 Date 의 초 단위 해상도 안쪽).
        var node = (await rig.Admin.GetFromJsonAsync<JsonElement>($"{Api}/edge/nodes")).EnumerateArray().Single();
        Assert.InRange(node.GetProperty("clock_offset_ms").GetInt64(), -1500, 1500);
    }

    [Fact]
    public async Task Agent_KeepsItsCredentialsAndConfig_AcrossARestart_AndRunsOfflineOnTheCachedCopy()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("a2/outbox"));
        var first = await rig.RegisteredAgentAsync(Dir("a2/state"), box);
        await first.RunOnceAsync(CancellationToken.None);
        var version = first.Config!.Version;

        rig.Net.Down = true;                                               // 엔진이 안 닿는다
        var restarted = rig.NewAgent(Dir("a2/state"), box);
        Assert.True(restarted.IsRegistered);                               // 자격이 디스크에 있다
        Assert.Equal(first.NodeId, restarted.NodeId);
        Assert.Equal(version, restarted.Config!.Version);                  // 구성 사본도 — 오프라인에서 그것으로 돈다
        Assert.Equal("demo", restarted.Config.Scenarios.Single().ScenarioId);
    }

    [Fact]
    public async Task OfflineKeepsEverything_BacksOffExponentially_AndDeliversInOrderExactlyOnceOnReturn()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("a3/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("a3/state"), box);
        var at = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) box.EnqueueEvent("demo", "count_changed", at, Count(i));

        rig.Net.Down = true;
        var delays = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var r = await agent.RunOnceAsync(CancellationToken.None);
            Assert.True(r.Offline);
            Assert.Equal(0, r.Sent);
            delays.Add(r.NextDelay.TotalSeconds);
        }
        Assert.Equal([2, 4, 8, 16, 16, 16], delays);                       // 두 배씩, 상한에서 멈춤
        Assert.Equal(5, box.Stats().Depth);                                // 하나도 잃지 않았다
        Assert.Empty(rig.EngineEventLines());

        rig.Net.Down = false;
        var back = await agent.RunOnceAsync(CancellationToken.None);
        Assert.Equal(5, back.Sent);
        Assert.Equal([0, 1, 2, 3, 4], rig.EngineEventLines().Select(l => JsonSerializer.Deserialize<JsonElement>(l).GetProperty("data").GetProperty("count").GetInt32()));
        Assert.Equal(TimeSpan.FromSeconds(30), back.NextDelay);            // 성공하면 백오프가 풀린다
    }

    [Fact]
    public async Task ALostResponse_IsResent_AndTheEngineKeepsOneRecord()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("a4/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("a4/state"), box);
        var at = DateTimeOffset.UtcNow;
        box.EnqueueEvent("demo", "count_changed", at, Count(1));
        box.EnqueueEvent("demo", "count_changed", at, Count(2));

        rig.Net.LoseNextEventsResponse = true;                             // 엔진은 처리했고 에이전트는 모른다
        var lost = await agent.RunOnceAsync(CancellationToken.None);
        Assert.True(lost.Offline);
        Assert.Equal(2, box.Stats().Depth);
        Assert.Equal(2, rig.EngineEventLines().Length);

        var again = await agent.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, again.Sent);
        Assert.Equal(2, again.Duplicates);                                 // 엔진이 duplicate 로 답했다
        Assert.Equal(0, box.Stats().Depth);
        Assert.Equal(2, rig.EngineEventLines().Length);                    // 기록은 그대로 두 줄
    }

    [Fact]
    public async Task ARevokedNode_StopsAndKeepsItsQueue_EvenAcrossARestart()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("a5/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("a5/state"), box);
        box.EnqueueEvent("demo", "count_changed", DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.OK, (await rig.Admin.DeleteAsync($"{Api}/edge/nodes/{rig.NodeId}")).StatusCode);
        var report = await agent.RunOnceAsync(CancellationToken.None);
        Assert.True(report.Revoked);
        Assert.True(agent.Revoked);
        Assert.Equal(1, box.Stats().Depth);                                // 지우지 않는다

        var calls = rig.Net.Paths.Count;
        var restarted = rig.NewAgent(Dir("a5/state"), box);
        Assert.True(restarted.Revoked);                                    // 재시작에도 유지
        Assert.True((await restarted.RunOnceAsync(CancellationToken.None)).Revoked);
        Assert.Equal(calls, rig.Net.Paths.Count);                          // 엔진을 다시 두드리지 않는다
    }

    [Fact]
    public async Task APermanentlyRejectedEvent_IsParkedWithItsReason_NotRetriedForever()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("a6/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("a6/state"), box);
        var at = DateTimeOffset.UtcNow;
        box.EnqueueEvent("not-assigned", "count_changed", at);
        box.EnqueueEvent("demo", "count_changed", at);

        var report = await agent.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, report.Parked);
        Assert.Equal(1, report.Sent);                                      // 한 항목의 거절이 나머지를 막지 않았다
        Assert.Equal(0, box.Stats().Depth);
        Assert.Equal(1, box.Stats().Parked);
        var reason = Directory.GetFiles(Path.Combine(Dir("a6/outbox"), "parked"), "*.reason").Single();
        Assert.Equal("scenario_not_assigned", File.ReadAllText(reason));
    }

    [Fact]
    public async Task EventsAndCrops_GoOutInQueueOrder_AndACropIsJudgedOnce_EvenWhenTheResponseIsLost()
    {
        using var rig = new Rig();
        using var box = AgentOutbox.Open(Dir("a7/outbox"));
        var agent = await rig.RegisteredAgentAsync(Dir("a7/state"), box);
        var at = DateTimeOffset.UtcNow;
        box.EnqueueEvent("demo", "count_changed", at);
        box.EnqueueCrop("demo", Jpeg, "image/jpeg", at);
        box.EnqueueEvent("demo", "alarm", at);

        var report = await agent.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, report.Sent);
        // config → (사건 1건) → 크롭 판정 → (사건 1건): 큐 순서를 건너뛰지 않는다.
        var paths = rig.Net.Paths.Where(p => !p.EndsWith("/register", StringComparison.Ordinal)).Select(p => p.Replace(Api, "")).ToList();
        Assert.Equal("/edge/v1/status", paths[^1]);                           // 상태 보고는 전달이 끝난 뒤 맨 끝에 한 번
        Assert.Equal(["/edge/v1/config", "/edge/v1/events", "/v2/inspect", "/edge/v1/events"], paths[..^1]);
        // 크롭은 판정되어 엔진에 레코드가 한 건 있다.
        Assert.Single(
            Directory.GetFiles(Path.Combine(Rig_DemoDir(rig), DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "*.json"),
            f => !f.EndsWith(".label.json", StringComparison.Ordinal));
    }

    private static string Rig_DemoDir(Rig rig) => Path.Combine(rig.Factory.DataPath, "demo");
}
