using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Edge.Agent;
using UVision.Edge.Agent.Host;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 컨테이너 안 에이전트 호스트 — 환경 변수 설정 해석, 등록(토큰 · 재시도 · 거절) → 사이클 → 종료 코드, 건강 파일. 실제 엔진에 붙인다.
/// </summary>
public sealed class EdgeAgentHostTests : IDisposable
{
    private const string Api = "/api/u-vision";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvision-host-" + Guid.NewGuid().ToString("N"));
    private string Dir(string name) => Path.Combine(_root, name);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // --- 설정 해석 -----------------------------------------------------------------------------

    [Fact]
    public void Options_MinimalEnvironment_UsesDefaults()
    {
        var o = HostOptions.FromEnvironment(Env(("UVISION_ENGINE_URL", "https://engine.example:8080")));

        Assert.Equal("https://engine.example:8080/api/u-vision", o.ApiBase);
        Assert.Equal(TimeSpan.FromSeconds(30), o.PollInterval);
        Assert.Equal("/data/state", o.StateDir);
        Assert.Equal("/data/outbox", o.OutboxDir);
        Assert.Null(o.RegistrationToken);
        Assert.Null(o.OutboxMaxBytes);
        Assert.False(o.SendsCredentialsInTheClear);
    }

    [Fact]
    public void Options_AllValues_AreRead_AndTrimmed()
    {
        var o = HostOptions.FromEnvironment(Env(
            ("UVISION_ENGINE_URL", " http://10.0.0.5:8080/ "), ("UVISION_API_BASE_PATH", "/vision"), ("UVISION_REGISTRATION_TOKEN", " abc.def "),
            ("UVISION_NODE_NAME", " 3라인 "), ("UVISION_STATE_DIR", "/s"), ("UVISION_OUTBOX_DIR", "/o"),
            ("UVISION_POLL_SECONDS", "10"), ("UVISION_OUTBOX_MAX_MB", "64")));

        Assert.Equal("http://10.0.0.5:8080/vision", o.ApiBase);
        Assert.Equal("abc.def", o.RegistrationToken);
        Assert.Equal("3라인", o.NodeName);
        Assert.Equal(TimeSpan.FromSeconds(10), o.PollInterval);
        Assert.Equal(64L * 1024 * 1024, o.OutboxMaxBytes);
        Assert.True(o.SendsCredentialsInTheClear);                 // 같은 기계 밖의 http — 경고 대상
    }

    [Fact]
    public void Options_LoopbackHttp_IsNotFlagged()
    {
        Assert.False(HostOptions.FromEnvironment(Env(("UVISION_ENGINE_URL", "http://localhost:8080"))).SendsCredentialsInTheClear);
        Assert.False(HostOptions.FromEnvironment(Env(("UVISION_ENGINE_URL", "http://127.0.0.1:8080"))).SendsCredentialsInTheClear);
    }

    [Fact]
    public void Options_ReportsEveryProblemAtOnce_NotJustTheFirst()
    {
        var ex = Assert.Throws<HostConfigException>(() => HostOptions.FromEnvironment(Env(
            ("UVISION_ENGINE_URL", "ftp://x"), ("UVISION_POLL_SECONDS", "1"), ("UVISION_OUTBOX_MAX_MB", "0"),
            ("UVISION_API_BASE_PATH", "api/"), ("UVISION_NODE_NAME", new string('n', 81)))));

        Assert.Equal(5, ex.Problems.Count);
        Assert.Contains(ex.Problems, p => p.Contains("UVISION_ENGINE_URL"));
        Assert.Contains(ex.Problems, p => p.Contains("UVISION_POLL_SECONDS"));
        Assert.Contains(ex.Problems, p => p.Contains("UVISION_OUTBOX_MAX_MB"));
        Assert.Contains(ex.Problems, p => p.Contains("UVISION_API_BASE_PATH"));
        Assert.Contains(ex.Problems, p => p.Contains("UVISION_NODE_NAME"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("engine.example:8080")]            // 스킴 없음
    [InlineData("https://engine.example/some/path")] // 경로는 UVISION_API_BASE_PATH 로
    [InlineData("https://engine.example?x=1")]
    public void Options_MissingOrBadEngineUrl_IsRejected(string? url)
    {
        Assert.Throws<HostConfigException>(() => HostOptions.FromEnvironment(Env(("UVISION_ENGINE_URL", url))));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("3601")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void Options_PollSecondsOutOfRange_IsRejected(string value)
    {
        Assert.Throws<HostConfigException>(() => HostOptions.FromEnvironment(Env(("UVISION_ENGINE_URL", "https://e"), ("UVISION_POLL_SECONDS", value))));
    }

    // --- 호스트 실행 -----------------------------------------------------------------------------

    private sealed class Engine : IDisposable
    {
        public PinFactory Factory { get; } = new();
        public HttpClient Admin { get; }

        public Engine()
        {
            Admin = Factory.CreateClient();
            Admin.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        }

        public async Task<string> IssueTokenAsync() =>
            (await (await Admin.PostAsJsonAsync($"{Api}/edge/tokens", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        /// <summary>노드가 아직 없으면 null — 등록은 호스트가 뜬 뒤에 일어난다.</summary>
        public async Task<JsonElement?> OnlyNodeAsync()
        {
            var nodes = (await Admin.GetFromJsonAsync<JsonElement>($"{Api}/edge/nodes")).EnumerateArray().ToList();
            return nodes.Count == 1 ? nodes[0] : null;
        }

        public HostOptions Options(string name, string? token, TimeSpan? poll = null) => new()
        {
            EngineUrl = new Uri("http://localhost"),
            RegistrationToken = token,
            NodeName = "host-test",
            StateDir = Path.Combine(Path.GetTempPath(), name + "-state"),
            OutboxDir = Path.Combine(Path.GetTempPath(), name + "-outbox"),
            PollInterval = poll ?? TimeSpan.FromMilliseconds(40),
        };

        public void Dispose()
        {
            Admin.Dispose();
            Factory.Dispose();
        }
    }

    /// <summary>호스트가 쓰는 두 폴더를 이 시험의 임시 루트 아래로 옮긴다(시험이 끝나면 지워진다).</summary>
    private HostOptions Under(HostOptions o, string name) => o with { StateDir = Dir(name + "/state"), OutboxDir = Dir(name + "/outbox") };

    private static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var value = await read();
            if (done(value) || Environment.TickCount64 > deadline) return value;
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Host_Registers_Delivers_ReportsStatus_WritesHealth_AndStopsCleanlyOnCancel()
    {
        using var engine = new Engine();
        var options = Under(engine.Options("h1", await engine.IssueTokenAsync()), "h1");
        // 호스트가 뜨기 전에 쌓인 사건(이전 실행이 남긴 outbox) — 등록 뒤 첫 사이클에 나간다.
        using (var seed = AgentOutbox.Open(options.OutboxDir))
            seed.EnqueueEvent("demo", "count_changed", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { count = 1 }));

        using var cts = new CancellationTokenSource();
        var log = new StringWriter();
        var run = AgentHost.RunAsync(options, engine.Factory.CreateDefaultClient(), log, cts.Token);

        // 배정이 없으면 엔진은 사건을 거절(parked)한다 — 먼저 노드를 찾아 배정한다.
        var node = await UntilAsync(async () => (await engine.Admin.GetFromJsonAsync<JsonElement>($"{Api}/edge/nodes")).EnumerateArray().ToList(), l => l.Count == 1);
        Assert.Single(node);
        var nodeId = node[0].GetProperty("node_id").GetString()!;
        Assert.Equal("host-test", node[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await engine.Admin.PutAsJsonAsync($"{Api}/edge/nodes/{nodeId}/scenarios", new { scenarios = new[] { "demo" } })).StatusCode);

        var status = await UntilAsync(engine.OnlyNodeAsync, n => n is { } x && x.TryGetProperty("status", out _));
        Assert.True(status!.Value.TryGetProperty("status", out var s));
        Assert.Equal(EdgeAgent.AgentVersion, s.GetProperty("agent_version").GetString());
        Assert.True(AgentHost.IsHealthy(options.StateDir, TimeSpan.FromSeconds(5)));

        cts.Cancel();
        Assert.Equal(ExitCodes.Ok, await run);
        Assert.Contains("등록했습니다", log.ToString());
        Assert.DoesNotContain(options.RegistrationToken!, log.ToString());          // 토큰은 로그에 남지 않는다
    }

    [Fact]
    public async Task Host_NotRegistered_AndNoToken_ExitsWithConfigError_WithoutTouchingTheEngine()
    {
        using var engine = new Engine();
        var options = Under(engine.Options("h2", token: null), "h2");

        var code = await AgentHost.RunAsync(options, engine.Factory.CreateDefaultClient(), new StringWriter(), CancellationToken.None);

        Assert.Equal(ExitCodes.Config, code);
        Assert.Empty(await engine.Admin.GetFromJsonAsync<JsonElement[]>($"{Api}/edge/nodes") ?? []);
    }

    [Fact]
    public async Task Host_RejectedToken_ExitsWithItsOwnCode_InsteadOfLoopingForever()
    {
        using var engine = new Engine();
        var options = Under(engine.Options("h3", "0000000000000000.wrong-wrong-wrong-wrong-wrong"), "h3");
        var log = new StringWriter();

        var code = await AgentHost.RunAsync(options, engine.Factory.CreateDefaultClient(), log, CancellationToken.None);

        Assert.Equal(ExitCodes.RegistrationRejected, code);
        Assert.Contains("새 토큰", log.ToString());
    }

    [Fact]
    public async Task Host_Restart_WhenAlreadyRegistered_NeedsNoToken_AndIgnoresAStaleOne()
    {
        using var engine = new Engine();
        var token = await engine.IssueTokenAsync();
        var options = Under(engine.Options("h4", token), "h4");

        using (var first = new CancellationTokenSource())
        {
            var run = AgentHost.RunAsync(options, engine.Factory.CreateDefaultClient(), new StringWriter(), first.Token);
            await UntilAsync(engine.OnlyNodeAsync, n => n is { } x && x.TryGetProperty("last_seen", out _));
            first.Cancel();
            Assert.Equal(ExitCodes.Ok, await run);
        }

        // 다시 띄움 — 토큰은 이미 썼다. 그래도 자격이 디스크에 있어 등록하지 않고 돈다.
        var log = new StringWriter();
        using var second = new CancellationTokenSource();
        var again = AgentHost.RunAsync(options, engine.Factory.CreateDefaultClient(), log, second.Token);
        await Task.Delay(300);
        second.Cancel();

        Assert.Equal(ExitCodes.Ok, await again);
        Assert.Contains("이미 등록되어", log.ToString());
        Assert.Single(await engine.Admin.GetFromJsonAsync<JsonElement[]>($"{Api}/edge/nodes") ?? []);   // 노드가 둘이 되지 않았다
    }

    [Fact]
    public async Task Host_WhenTheEngineRevokesTheNode_StopsWithTheRevokedCode_AndKeepsTheOutbox()
    {
        using var engine = new Engine();
        var options = Under(engine.Options("h5", await engine.IssueTokenAsync()), "h5");
        var run = AgentHost.RunAsync(options, engine.Factory.CreateDefaultClient(), new StringWriter(), CancellationToken.None);
        var node = await UntilAsync(engine.OnlyNodeAsync, n => n is { } x && x.TryGetProperty("last_seen", out _));

        await engine.Admin.DeleteAsync($"{Api}/edge/nodes/{node!.Value.GetProperty("node_id").GetString()}");

        Assert.Equal(ExitCodes.Revoked, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(Directory.Exists(options.OutboxDir));
    }

    /// <summary>처음 N 번의 등록 요청을 네트워크 실패로 만든다 — 엔진이 에이전트보다 늦게 뜨는 기동 순서를 흉내 낸다.</summary>
    private sealed class FlakyRegister(int failures) : DelegatingHandler
    {
        public int Attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/edge/v1/register", StringComparison.Ordinal) && Interlocked.Increment(ref Attempts) <= failures)
                throw new HttpRequestException("engine not up yet");
            return base.SendAsync(request, ct);
        }
    }

    /// <summary>대기가 곧바로 끝나는 시계 — 재시도 간격(2·4·8 초)을 시험이 기다리지 않게.</summary>
    private sealed class InstantDelays : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task Host_RegistrationRetries_WhileTheEngineIsUnreachable_ThenSucceeds()
    {
        using var engine = new Engine();
        var options = Under(engine.Options("h6", await engine.IssueTokenAsync()), "h6");
        var flaky = new FlakyRegister(failures: 3);
        var log = new StringWriter();
        using var cts = new CancellationTokenSource();

        var run = AgentHost.RunAsync(options, engine.Factory.CreateDefaultClient(flaky), log, cts.Token, new InstantDelays());
        var node = await UntilAsync(async () => (await engine.Admin.GetFromJsonAsync<JsonElement>($"{Api}/edge/nodes")).EnumerateArray().ToList(), l => l.Count == 1);
        cts.Cancel();

        Assert.Equal(ExitCodes.Ok, await run);
        Assert.Single(node);
        Assert.Equal(4, flaky.Attempts);                                              // 세 번 실패 + 한 번 성공
        Assert.Equal(3, log.ToString().Split("뒤 다시 시도합니다").Length - 1);
    }

    [Fact]
    public async Task Host_CancelledWhileRegistering_ExitsCleanly()
    {
        using var engine = new Engine();
        var options = Under(engine.Options("h7", await engine.IssueTokenAsync()), "h7");
        using var cts = new CancellationTokenSource();
        var run = AgentHost.RunAsync(options, engine.Factory.CreateDefaultClient(new FlakyRegister(failures: int.MaxValue)), new StringWriter(), cts.Token);
        await Task.Delay(150);
        cts.Cancel();

        Assert.Equal(ExitCodes.Ok, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // --- 건강 파일 -------------------------------------------------------------------------------

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Health_FreshIsHealthy_StaleOrMissingOrGarbageIsNot()
    {
        var dir = Dir("health");
        Directory.CreateDirectory(dir);
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        Assert.False(AgentHost.IsHealthy(dir, TimeSpan.FromMinutes(1), new FixedClock(now)));               // 파일 없음

        File.WriteAllText(Path.Combine(dir, AgentHost.HealthFileName), now.AddSeconds(-30).ToString("o"));
        Assert.True(AgentHost.IsHealthy(dir, TimeSpan.FromMinutes(1), new FixedClock(now)));
        Assert.False(AgentHost.IsHealthy(dir, TimeSpan.FromSeconds(10), new FixedClock(now)));              // 낡음

        File.WriteAllText(Path.Combine(dir, AgentHost.HealthFileName), "not a time");
        Assert.False(AgentHost.IsHealthy(dir, TimeSpan.FromMinutes(1), new FixedClock(now)));
    }

    [Theory]
    [InlineData(30, 630)]     // 기본 폴링: 백오프 상한(5 분)을 두 번 지나는 값이 바닥
    [InlineData(300, 900)]    // 폴링이 길면 그 3 배
    public void HealthMaxAge_IsNeverShorterThanTheOfflineBackoff(int pollSeconds, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), HostProgram.HealthMaxAge(pollSeconds));
    }
}
