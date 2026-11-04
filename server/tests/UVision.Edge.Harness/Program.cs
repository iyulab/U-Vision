using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Edge.Agent;

// 엣지 에이전트 하니스 — 원장 D-102 · edge.md §5 의 «하드웨어 없이» 완료 증거 다섯을 실행 출력으로 남긴다.
//   evidence [--scale N] [--engine-dll PATH] [--work DIR]   증거 1~5 를 차례로 (실패가 하나라도 있으면 종료 코드 1)
//   (증거 7 은 실제 «에이전트 호스트» 프로세스를 띄우므로 UVision.Edge.Agent.Host 도 빌드돼 있어야 한다 — 솔루션을 먼저 Release 로 빌드한다)
//   agent    --engine URL --state DIR --outbox DIR --replay FILE [--start N] [--delay-ms N] [--clock-skew-seconds S]
//            파일 재생 가짜 워커 + 에이전트 루프 (증거 1 · 3 이 자식 프로세스로 띄우고 죽인다)

return args.FirstOrDefault() switch
{
    "agent" => await AgentCommand.RunAsync(args[1..]),
    "evidence" => await Evidence.RunAsync(args[1..]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: evidence [--scale N] [--engine-dll PATH] [--work DIR] | agent --engine URL --state DIR --outbox DIR --replay FILE [--start N] [--delay-ms N] [--clock-skew-seconds S]");
    return 2;
}

/// <summary>노드 시계를 일부러 어긋나게 하는 시계 — 서버보다 <c>skew</c> 만큼 뒤(음수)/앞선 노드.</summary>
sealed class SkewedClock(TimeSpan skew) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + skew;
}

static class Cli
{
    public static string? Opt(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}

/// <summary>파일 재생 가짜 워커 + 에이전트 — 재생 파일의 줄마다 사건을 outbox 에 넣고 에이전트 루프가 엔진으로 비운다.</summary>
static class AgentCommand
{
    public static async Task<int> RunAsync(string[] a)
    {
        var engine = Cli.Opt(a, "--engine")!;
        var state = Cli.Opt(a, "--state")!;
        var outboxDir = Cli.Opt(a, "--outbox")!;
        var replay = Cli.Opt(a, "--replay")!;
        var start = int.Parse(Cli.Opt(a, "--start") ?? "0", CultureInfo.InvariantCulture);
        var delayMs = int.Parse(Cli.Opt(a, "--delay-ms") ?? "0", CultureInfo.InvariantCulture);
        var skew = TimeSpan.FromSeconds(double.Parse(Cli.Opt(a, "--clock-skew-seconds") ?? "0", CultureInfo.InvariantCulture));
        TimeProvider clock = skew == TimeSpan.Zero ? TimeProvider.System : new SkewedClock(skew);

        using var http = new HttpClient();
        var box = AgentOutbox.Open(outboxDir, clock: clock);
        var agent = new EdgeAgent(
            new EngineClient(http, engine.TrimEnd('/') + "/api/u-vision", clock), box, state,
            new AgentOptions { PollInterval = TimeSpan.FromMilliseconds(150), BatchSize = 50, BackoffBase = TimeSpan.FromMilliseconds(200), BackoffCap = TimeSpan.FromSeconds(1), Jitter = 0 },
            clock);
        if (!agent.IsRegistered) { Console.Error.WriteLine("not registered"); return 3; }

        using var cts = new CancellationTokenSource();
        var loop = agent.RunAsync(cts.Token);
        var lines = File.ReadAllLines(replay);
        for (var i = start; i < lines.Length; i++)
        {
            var row = JsonDocument.Parse(lines[i]).RootElement;
            box.EnqueueEvent(row.GetProperty("scenario_id").GetString()!, row.GetProperty("kind").GetString()!, clock.GetUtcNow(), row.GetProperty("data").Clone());
            Console.WriteLine($"ENQUEUED {i}");
            if (delayMs > 0) await Task.Delay(delayMs);
        }
        // 재생이 끝나면 큐가 빌 때까지(또는 회수될 때까지) 돈다.
        while (box.Stats().Depth > 0 && !agent.Revoked) await Task.Delay(100);
        Console.WriteLine(agent.Revoked ? "REVOKED" : "DRAINED");
        cts.Cancel();
        await loop;
        box.Dispose();
        return 0;
    }
}

/// <summary>실제 엔진 프로세스 하나 — 데이터 폴더는 호출부가 정하고, 죽였다가 같은 폴더로 다시 띄울 수 있다.</summary>
sealed class EngineProcess(string dll, string dataDir, int port, string pin) : IDisposable
{
    private Process? _p;
    public string Url => $"http://127.0.0.1:{port}";

    public async Task StartAsync()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls {Url}")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.Environment["ADMIN_PIN"] = pin;
        psi.Environment["Storage__DataPath"] = dataDir;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        _p = Process.Start(psi)!;
        _p.OutputDataReceived += (_, _) => { }; _p.ErrorDataReceived += (_, _) => { };
        _p.BeginOutputReadLine(); _p.BeginErrorReadLine();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 100; i++)
        {
            try { if ((await http.GetAsync($"{Url}/api/u-vision/health")).IsSuccessStatusCode) return; } catch (HttpRequestException) { } catch (TaskCanceledException) { }
            await Task.Delay(200);
        }
        throw new InvalidOperationException("엔진이 올라오지 않았다");
    }

    public void Kill() { try { _p?.Kill(entireProcessTree: true); _p?.WaitForExit(5000); } catch (InvalidOperationException) { } _p = null; }
    public void Dispose() => Kill();
}

static class Evidence
{
    private const string Pin = "1234";
    private static int _failed;

    private static void Pass(string id, string text) => Console.WriteLine($"[{id}] PASS  {text}");
    private static void Fail(string id, string text) { _failed++; Console.WriteLine($"[{id}] FAIL  {text}"); }
    private static void Check(string id, bool ok, string text) { if (ok) Pass(id, text); else Fail(id, text); }

    public static async Task<int> RunAsync(string[] a)
    {
        var scale = int.Parse(Cli.Opt(a, "--scale") ?? "100000", CultureInfo.InvariantCulture);
        var work = Cli.Opt(a, "--work") ?? Path.Combine(Path.GetTempPath(), "uvision-edge-harness-" + Guid.NewGuid().ToString("N")[..8]);
        var dll = Cli.Opt(a, "--engine-dll") ?? FindEngineDll();
        Directory.CreateDirectory(work);
        Console.WriteLine($"# edge harness — {DateTimeOffset.UtcNow:o}  work={work}  engine={dll}  scale={scale}");

        var data = Path.Combine(work, "engine-data");
        var port = Random.Shared.Next(20000, 40000);
        using var engine = new EngineProcess(dll, data, port, Pin);
        await engine.StartAsync();
        using var admin = new HttpClient { BaseAddress = new Uri(engine.Url) };
        admin.DefaultRequestHeaders.Add("X-Admin-Pin", Pin);
        Check("E0", (await admin.PostAsJsonAsync("/api/u-vision/scenarios", new { scenario_id = "line1", name = "line1", criteria = "x" })).StatusCode == HttpStatusCode.Created, "엔진 프로세스 기동 · 시나리오 line1 생성");

        try
        {
            await E1_KillAgent(engine, admin, data, work);
            var node2 = await E2_Disconnect(engine, admin, data, work);
            await E3_ClockSkew(engine, admin, data, work);
            await E4_Scale(engine, admin, data, work, scale);
            await E5_TokenAndRevoke(engine, admin, work, node2);
            await E6_StatusReport(engine, admin, data, work);
            await E7_WorkerSocket(engine, admin, data, work);
        }
        finally
        {
            engine.Kill();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine(_failed == 0 ? "# ALL PASS" : $"# {_failed} FAILED");
        return _failed == 0 ? 0 : 1;
    }

    // --- 공통 ----------------------------------------------------------------------------------

    private static string FindEngineDll()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            foreach (var cfg in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(d.FullName, "src", "UVision.Api", "bin", cfg, "net10.0", "UVision.Api.dll");
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException("UVision.Api.dll 을 찾지 못했다 — 먼저 빌드하거나 --engine-dll 을 준다");
    }

    private sealed record Registered(string NodeId, string Secret, string StateDir, string OutboxDir);

    private static async Task<Registered> RegisterNodeAsync(HttpClient admin, string engineUrl, string work, string name)
    {
        var token = (await (await admin.PostAsJsonAsync("/api/u-vision/edge/tokens", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var state = Path.Combine(work, name, "state");
        var outbox = Path.Combine(work, name, "outbox");
        using var http = new HttpClient();
        using var box = AgentOutbox.Open(outbox);
        var agent = new EdgeAgent(new EngineClient(http, engineUrl + "/api/u-vision"), box, state);
        await agent.RegisterAsync(token, name, CancellationToken.None);
        await admin.PutAsJsonAsync($"/api/u-vision/edge/nodes/{agent.NodeId}/scenarios", new { scenarios = new[] { "line1" } });
        var creds = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "credentials.json"))).RootElement;
        return new Registered(agent.NodeId!, creds.GetProperty("secret").GetString()!, state, outbox);
    }

    private static string WriteReplay(string path, int count)
    {
        File.WriteAllLines(path, Enumerable.Range(0, count).Select(i =>
            JsonSerializer.Serialize(new { scenario_id = "line1", kind = "count_changed", data = new { n = i } })));
        return path;
    }

    /// <summary>엔진이 아직 쓰는 파일도 읽는다(쓰기 공유) — 마지막 줄이 쓰는 중이면 잘린 채 올 수 있어 완결된 줄만 돌려준다.</summary>
    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var lastNewline = text.LastIndexOf('\n');
        return lastNewline < 0 ? [] : text[..lastNewline].Split('\n');
    }

    private static List<JsonElement> EngineEvents(string data, string nodeId)
    {
        var dir = Path.Combine(data, "edge", "nodes", nodeId, "events");
        return !Directory.Exists(dir) ? [] : Directory.GetFiles(dir, "*.jsonl")
            .SelectMany(ReadSharedLines).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static Process StartAgent(string engineUrl, Registered n, string replay, int start, int delayMs, double skewSeconds = 0)
    {
        var self = Environment.ProcessPath!;
        var dll = Path.ChangeExtension(typeof(Evidence).Assembly.Location, ".dll");
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" agent --engine {engineUrl} --state \"{n.StateDir}\" --outbox \"{n.OutboxDir}\" --replay \"{replay}\" --start {start} --delay-ms {delayMs} --clock-skew-seconds {skewSeconds.ToString(CultureInfo.InvariantCulture)}")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        _ = self;
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private static string FindHostDll()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            foreach (var cfg in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(d.FullName, "src", "UVision.Edge.Agent.Host", "bin", cfg, "net10.0", "uvision-edge-agent.dll");
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException("uvision-edge-agent.dll(에이전트 호스트)을 찾지 못했다 — 솔루션을 먼저 빌드한다");
    }

    /// <summary>실제 에이전트 호스트 프로세스 — 환경 변수로만 설정한다(컨테이너와 같은 길). 표준 출력은 줄 목록으로 모은다.</summary>
    private sealed class HostProcess : IDisposable
    {
        private readonly Process _p;
        private readonly List<string> _lines = [];

        public HostProcess(string engineUrl, string? token, string state, string outbox, string socket)
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{FindHostDll()}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.Environment["UVISION_ENGINE_URL"] = engineUrl;
            if (token is not null) psi.Environment["UVISION_REGISTRATION_TOKEN"] = token;
            psi.Environment["UVISION_NODE_NAME"] = "e7-host";
            psi.Environment["UVISION_STATE_DIR"] = state;
            psi.Environment["UVISION_OUTBOX_DIR"] = outbox;
            psi.Environment["UVISION_POLL_SECONDS"] = "5";   // 하한
            psi.Environment["UVISION_WORKER_SOCKET"] = socket;
            _p = Process.Start(psi)!;
            _p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_lines) _lines.Add(e.Data); };
            _p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_lines) _lines.Add("[stderr] " + e.Data); };
            _p.BeginOutputReadLine(); _p.BeginErrorReadLine();
        }

        public bool HasExited => _p.HasExited;
        public bool LogContains(string text) { lock (_lines) return _lines.Any(l => l.Contains(text, StringComparison.Ordinal)); }
        public string Tail() { lock (_lines) return string.Join(" | ", _lines.TakeLast(4)); }
        public void Kill() { try { _p.Kill(entireProcessTree: true); _p.WaitForExit(5000); } catch (InvalidOperationException) { } }
        public void Dispose() { Kill(); _p.Dispose(); }
    }

    /// <summary>가짜 워커 — 호스트가 듣는 유닉스 소켓에 NDJSON 줄을 흘린다.</summary>
    private sealed class FakeWorker : IDisposable
    {
        private readonly System.Net.Sockets.Socket _s = new(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);

        public static async Task<FakeWorker> ConnectAsync(string path)
        {
            var w = new FakeWorker();
            await w._s.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            return w;
        }

        public Task SendAsync(string text) => _s.SendAsync(System.Text.Encoding.UTF8.GetBytes(text));

        /// <summary>선반 구역 안(0.3, 0.5)의 상자 <paramref name="boxes"/>개와 사람 <paramref name="people"/>명이 있는 프레임.</summary>
        public static string Frame(DateTimeOffset t, int seq, int boxes, int people)
        {
            var tracks = Enumerable.Range(0, boxes).Select(i => new { id = $"b{i}", @class = "box", score = 0.9, box = new[] { 0.2 + i * 0.01, 0.4, 0.1, 0.1 } })
                .Concat(Enumerable.Range(0, people).Select(i => new { id = $"p{i}", @class = "person", score = 0.9, box = new[] { 0.25, 0.3, 0.1, 0.2 } }));
            return JsonSerializer.Serialize(new { v = 1, type = "tracks", source_id = "dock-1", seq, t = t.ToString("o"), tracks }) + "\n";
        }

        public void Dispose() => _s.Dispose();
    }

    // --- 증거 7: 호스트 프로세스 + 가짜 워커 소켓 → 엔진 프로세스 (프로세스 경계) -------------------------------

    private static async Task E7_WorkerSocket(EngineProcess engine, HttpClient admin, string data, string work)
    {
        // 스트림 감시 시나리오: 선반 구역의 상자 개수(3프레임 안정이면 사건) + 사람이 2초 있으면 경보.
        var created = await admin.PostAsJsonAsync("/api/u-vision/scenarios", new
        {
            scenario_id = "watch", name = "watch", type = "monitoring",
            sources = new[] { new { id = "cam1", camera_ref = "dock-1", zones = new[] { new { id = "shelf", polygon = new[] { new[] { 0.1, 0.1 }, new[] { 0.5, 0.1 }, new[] { 0.5, 0.9 }, new[] { 0.1, 0.9 } } } } } },
            rules = new object[]
            {
                new { id = "stock", kind = "count", source = "cam1", zone = "shelf", @class = "box", stable_frames = 3 },
                new { id = "intruder", kind = "alarm", source = "cam1", zone = "shelf", @class = "person", min_count = 1, for_seconds = 2 },
            },
        });
        var token = (await (await admin.PostAsJsonAsync("/api/u-vision/edge/tokens", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var state = Path.Combine(work, "e7", "state");
        var outbox = Path.Combine(work, "e7", "outbox");
        var socketDir = Path.Combine(Path.GetTempPath(), "uvh7-" + Guid.NewGuid().ToString("N")[..6]);
        var socket = Path.Combine(socketDir, "w.sock");

        var host = new HostProcess(engine.Url, token, state, outbox, socket);
        try
        {
            // 호스트가 스스로 등록한다 → 노드가 목록에 나타나면 시나리오를 배정하고, 다음 폴링(5초 이내)에 규칙이 선다.
            string? nodeId = null;
            await WaitAsync(() =>
            {
                var nodes = admin.GetFromJsonAsync<JsonElement>("/api/u-vision/edge/nodes").GetAwaiter().GetResult();
                nodeId = nodes.EnumerateArray().Where(n => n.GetProperty("name").GetString() == "e7-host").Select(n => n.GetProperty("node_id").GetString()).FirstOrDefault();
                return nodeId is not null;
            }, TimeSpan.FromSeconds(30));
            if (nodeId is not null)
                await admin.PutAsJsonAsync($"/api/u-vision/edge/nodes/{nodeId}/scenarios", new { scenarios = new[] { "watch" } });
            var ruled = await WaitAsync(() => host.LogContains("규칙 2개 · 카메라 dock-1") || host.HasExited, TimeSpan.FromSeconds(30));
            Check("E7a", created.StatusCode == HttpStatusCode.Created && nodeId is not null && ruled && !host.HasExited,
                $"호스트 프로세스가 스스로 등록하고 구성을 받아 규칙 2개를 세움 — {(host.HasExited ? "호스트 종료: " + host.Tail() : "로그: 규칙 2개 · 카메라 dock-1")}");
            if (nodeId is null || !ruled) return;

            var t0 = DateTimeOffset.UtcNow;
            // 상자 1개 3프레임(안정 → 기준선 사건) + 사람이 3초(경보).
            await WaitAsync(() => File.Exists(socket), TimeSpan.FromSeconds(10));
            using (var worker = await FakeWorker.ConnectAsync(socket))
            {
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < 4; i++) sb.Append(FakeWorker.Frame(t0.AddSeconds(i), i, boxes: 1, people: i >= 1 ? 1 : 0));
                sb.Append("this is not json\n");   // 깨진 줄은 수신을 막지 않는다
                await worker.SendAsync(sb.ToString());
                var arrived = await WaitAsync(() =>
                {
                    var kinds = EngineEvents(data, nodeId).Select(e => e.GetProperty("kind").GetString()).ToList();
                    return kinds.Contains("count_changed") && kinds.Contains("alarm_raised");
                }, TimeSpan.FromSeconds(30));
                var first = EngineEvents(data, nodeId);
                var count = first.FirstOrDefault(e => e.GetProperty("kind").GetString() == "count_changed");
                Check("E7b", arrived && count.ValueKind == JsonValueKind.Object && count.GetProperty("data").GetProperty("count").GetInt32() == 1,
                    $"가짜 워커의 프레임이 호스트 프로세스의 규칙을 거쳐 엔진 프로세스에 도착: {string.Join(", ", first.Select(e => e.GetProperty("kind").GetString()))}");
            }

            // 워커가 연결을 끊으면(소켓 닫힘) 소실이 사건이 된다.
            var lost = await WaitAsync(() => EngineEvents(data, nodeId).Any(e => e.GetProperty("kind").GetString() == "source_state"
                && e.GetProperty("data").GetProperty("state").GetString() == "no_signal"), TimeSpan.FromSeconds(30));
            Check("E7c", lost, "워커 연결을 끊자 source_state(no_signal) 사건이 엔진에 도착");

            // 호스트를 강제 종료했다 같은 상태 폴더로 다시 띄운다 — 등록·구성·outbox 가 디스크에서 돌아오고 사건이 중복 · 유실 없이 이어진다.
            var beforeKill = EngineEvents(data, nodeId).Select(e => e.GetProperty("event_id").GetString()).ToList();
            host.Kill();
            host.Dispose();
            host = new HostProcess(engine.Url, token: null, state, outbox, socket);
            var revived = await WaitAsync(() => host.LogContains("규칙 2개 · 카메라 dock-1") || host.HasExited, TimeSpan.FromSeconds(30));
            await WaitAsync(() => File.Exists(socket), TimeSpan.FromSeconds(10));
            var t1 = DateTimeOffset.UtcNow;
            using (var worker = await FakeWorker.ConnectAsync(socket))
            {
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < 4; i++) sb.Append(FakeWorker.Frame(t1.AddSeconds(i), 100 + i, boxes: 2, people: 0));   // 상자가 2개로 안정 → 변화 사건
                await worker.SendAsync(sb.ToString());
                await WaitAsync(() => EngineEvents(data, nodeId).Any(e => e.GetProperty("kind").GetString() == "count_changed"
                    && e.GetProperty("data").GetProperty("count").GetInt32() == 2), TimeSpan.FromSeconds(30));
            }
            var all = EngineEvents(data, nodeId);
            var ids = all.Select(e => e.GetProperty("event_id").GetString()).ToList();
            var kept = beforeKill.All(ids.Contains);
            var change = all.FirstOrDefault(e => e.GetProperty("kind").GetString() == "count_changed" && e.GetProperty("data").GetProperty("count").GetInt32() == 2);
            Check("E7d", revived && kept && ids.Distinct().Count() == ids.Count && change.ValueKind == JsonValueKind.Object,
                $"호스트를 강제 종료했다 다시 띄운 뒤에도 이어짐 — 먼저 도착한 사건 {beforeKill.Count}건 유지 · 전체 {ids.Count}건 · 중복 id {ids.Count - ids.Distinct().Count()}건 · 새 변화 사건(상자 2) {(change.ValueKind == JsonValueKind.Object ? "도착" : "없음")}");

            // 운영자가 보는 상태 보고에 워커 수신 상태가 닿는다.
            JsonElement? worker2 = null;
            await WaitAsync(() =>
            {
                var nodes = admin.GetFromJsonAsync<JsonElement>("/api/u-vision/edge/nodes").GetAwaiter().GetResult();
                var mine = nodes.EnumerateArray().FirstOrDefault(n => n.GetProperty("node_id").GetString() == nodeId);
                if (mine.ValueKind == JsonValueKind.Object && mine.TryGetProperty("status", out var st) && st.TryGetProperty("worker", out var w) && w.GetProperty("rules").GetInt32() == 2)
                {
                    worker2 = w.Clone();
                    return true;
                }
                return false;
            }, TimeSpan.FromSeconds(30));
            Check("E7e", worker2 is { } w2 && w2.GetProperty("rules").GetInt32() == 2,
                worker2 is { } w3 ? $"노드 목록의 상태 보고에 워커 수신 상태가 보임: 규칙 {w3.GetProperty("rules").GetInt32()} · 프레임 {w3.GetProperty("frames").GetInt64()} · 버린 줄 {w3.GetProperty("dropped").GetInt64()}" : "워커 수신 상태가 노드 목록에 나타나지 않음");
        }
        finally
        {
            host.Dispose();
            try { Directory.Delete(socketDir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> cond, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    // --- 증거 1: 에이전트를 강제 종료해도 outbox 가 복원된다 ----------------------------------------------

    private static async Task E1_KillAgent(EngineProcess engine, HttpClient admin, string data, string work)
    {
        const int total = 600;
        // 종료 시점에 outbox 에 남은 항목이 있어야 «복원» 증거가 된다 — 에이전트가 폴링 사이사이 대기열을 비우는 타이밍이라, 쌓인 순간에
        // 죽이지 못했으면(남은 0건) 새 노드로 다시 한다(최대 5번).
        Registered node = null!; string replay = ""; int deliveredAtKill = 0, pending = 0, enqueued = 0, pendingAlreadyAtEngine = 0;
        var attempt = 0;
        for (; attempt < 5 && pending == 0; attempt++)
        {
            node = await RegisterNodeAsync(admin, engine.Url, work, "e1-" + attempt);
            replay = WriteReplay(Path.Combine(work, $"e1-{attempt}.jsonl"), total);
            var first = StartAgent(engine.Url, node, replay, start: 0, delayMs: 1);
            await WaitAsync(() => EngineEvents(data, node.NodeId).Count >= 100, TimeSpan.FromSeconds(60));
            first.Kill(entireProcessTree: true);          // 강제 종료 — 정리 코드가 돌 기회가 없다
            first.WaitForExit(5000);
            deliveredAtKill = EngineEvents(data, node.NodeId).Count;
            using var box = AgentOutbox.Open(node.OutboxDir);
            var left = box.Peek(int.MaxValue);
            pending = left.Count;
            var engineNs = EngineEvents(data, node.NodeId).Select(e => e.GetProperty("data").GetProperty("n").GetInt32()).ToHashSet();
            var pendingNs = left.Select(i2 => i2.Data!.Value.GetProperty("n").GetInt32()).ToList();
            pendingAlreadyAtEngine = pendingNs.Count(engineNs.Contains);   // 엔진은 받았는데 ack 를 못 하고 죽은 항목
            // 워커가 이어 받을 지점 = 지금까지 큐에 넣은 가장 큰 번호 + 1 (엔진이 받은 것과 outbox 에 남은 것 모두에서).
            enqueued = engineNs.Concat(pendingNs).DefaultIfEmpty(-1).Max() + 1;
        }
        Check("E1a", pending > 0 && deliveredAtKill is > 0 and < total,
            $"강제 종료 시점({attempt}번째 시도): 엔진이 받은 {deliveredAtKill}건 · outbox 에 남은 {pending}건(그중 엔진이 이미 받은 것 {pendingAlreadyAtEngine}) · 큐에 넣은 {enqueued}건 (총 {total} 중 재생 중)");

        // 같은 outbox · 같은 자격으로 다시 띄운다 — 재생은 큐에 넣은 곳(enqueued)부터 이어진다.
        var second = StartAgent(engine.Url, node, replay, start: enqueued, delayMs: 0);
        var done = await WaitAsync(() => EngineEvents(data, node.NodeId).Count >= total && second.HasExited, TimeSpan.FromSeconds(90));
        if (!second.HasExited) second.Kill(entireProcessTree: true);

        var events = EngineEvents(data, node.NodeId);
        var ns = events.Select(e => e.GetProperty("data").GetProperty("n").GetInt32()).ToList();
        Check("E1b", done && ns.Order().SequenceEqual(Enumerable.Range(0, total)),
            $"재시작 뒤 엔진에 {ns.Count}건 — 0..{total - 1} 이 각각 정확히 한 번(손실 0 · 중복 0)");
        var seqs = events.Select(e => e.GetProperty("seq").GetInt64()).ToList();
        var distinct = seqs.Distinct().Order().ToList();
        var holes = Enumerable.Range(0, (int)distinct.Max() + 1).Count(n => !distinct.Contains(n));
        var reused = seqs.Count - distinct.Count;
        // 설계상 허용: 비정상 종료 뒤 고수위 표지가 낡았으면 이미 쓴 번호가 다시 쓰일 수 있다(엔진은 구멍이 아닌 늦은 도착으로 센다).
        // 보장하는 것은 «번호를 건너뛰지 않는다» — 구멍이 없다.
        Check("E1c", holes == 0, $"seq 구멍 {holes}개(0..{distinct.Max()}) · 비정상 종료로 다시 쓰인 번호 {reused}개 — 재사용은 허용(고수위 표지가 느슨), 건너뛰기는 없음");
    }

    // --- 증거 2: 끊김 → 재접속, 같은 id 두 번 = 한 건 + duplicate, 엔진 재시작 뒤에도 ---------------------------

    private static async Task<Registered> E2_Disconnect(EngineProcess engine, HttpClient admin, string data, string work)
    {
        var node = await RegisterNodeAsync(admin, engine.Url, work, "e2");
        using var http = new HttpClient();
        using var box = AgentOutbox.Open(node.OutboxDir);
        var opts = new AgentOptions { Jitter = 0, BackoffBase = TimeSpan.FromMilliseconds(10), BackoffCap = TimeSpan.FromMilliseconds(50) };
        var agent = new EdgeAgent(new EngineClient(http, engine.Url + "/api/u-vision"), box, node.StateDir, opts);
        await agent.RunOnceAsync(CancellationToken.None);          // 구성 사본 확보

        engine.Kill();                                             // 엔진이 사라진다 — 연결이 끊긴 상태
        for (var i = 0; i < 50; i++) box.EnqueueEvent("line1", "count_changed", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { n = i }));
        var offline = await agent.RunOnceAsync(CancellationToken.None);
        Check("E2a", offline.Offline && box.Stats().Depth == 50, $"엔진 없음: 에이전트는 오프라인으로 멈추고 50건을 그대로 쥐고 있음(depth={box.Stats().Depth})");

        await engine.StartAsync();                                 // 같은 데이터 폴더로 엔진을 다시 띄운다
        var back = await agent.RunOnceAsync(CancellationToken.None);
        var events = EngineEvents(data, node.NodeId);
        Check("E2b", back.Sent == 50 && events.Count == 50
                && events.Select(e => e.GetProperty("seq").GetInt64()).SequenceEqual(Enumerable.Range(0, 50).Select(x => (long)x)),
            $"재접속: 50건이 순서대로(seq 0..49) 한 번씩 도착 (sent={back.Sent})");

        // 같은 event_id 를 다시 보낸다 — 직접 HTTP 로.
        var ids = events.Take(5).Select(e => e.GetProperty("event_id").GetString()!).ToList();
        var dup1 = await PostEventsAsync(http, engine.Url, node, ids);
        Check("E2c", dup1.All(s => s == "duplicate") && EngineEvents(data, node.NodeId).Count == 50, $"같은 event_id 5건을 다시 보냄 → {string.Join(",", dup1.Distinct())} · 엔진 기록은 그대로 50건");

        engine.Kill();
        await engine.StartAsync();                                 // 엔진 재시작 뒤에도
        var dup2 = await PostEventsAsync(http, engine.Url, node, ids);
        Check("E2d", dup2.All(s => s == "duplicate") && EngineEvents(data, node.NodeId).Count == 50, $"엔진 프로세스를 죽였다 다시 띄운 뒤에도 → {string.Join(",", dup2.Distinct())} · 기록 50건");
        return node;
    }

    private static async Task<List<string>> PostEventsAsync(HttpClient http, string url, Registered node, List<string> ids)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/u-vision/edge/v1/events")
        {
            Content = JsonContent.Create(new
            {
                events = ids.Select((id, i) => new { event_id = id, seq = i, scenario_id = "line1", kind = "count_changed", device_time = DateTimeOffset.UtcNow.ToString("o"), data = new { n = i } }),
            }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{node.NodeId}.{node.Secret}");
        var body = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        return [.. body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("status").GetString()!)];
    }

    // --- 증거 3: 시계가 어긋난 가짜 노드의 이중 기록 · 오프셋 표시 ---------------------------------------------

    private static async Task E3_ClockSkew(EngineProcess engine, HttpClient admin, string data, string work)
    {
        var node = await RegisterNodeAsync(admin, engine.Url, work, "e3");
        var replay = WriteReplay(Path.Combine(work, "e3.jsonl"), 6);
        var p = StartAgent(engine.Url, node, replay, 0, 50, skewSeconds: -600);   // 노드 시계가 서버보다 10 분 뒤
        await WaitAsync(() => p.HasExited, TimeSpan.FromSeconds(30));
        if (!p.HasExited) p.Kill(entireProcessTree: true);

        var row = (await admin.GetFromJsonAsync<JsonElement>("/api/u-vision/edge/nodes")).EnumerateArray().Single(n => n.GetProperty("node_id").GetString() == node.NodeId);
        var offset = row.TryGetProperty("clock_offset_ms", out var o) ? o.GetInt64() : 0;
        var skewed = row.TryGetProperty("clock_skewed", out var s) && s.GetBoolean();
        var events = EngineEvents(data, node.NodeId);
        Check("E3a", events.Count == 6 && Math.Abs(offset - 600_000) < 2_500 && skewed,
            $"노드 시계 −600 s: 보고된 오프셋 {offset} ms (기대 ≈ +600000 ± Date 해상도) · clock_skewed={skewed} · 사건 {events.Count}건 모두 수신(거절 없음)");

        var e = events[^1];
        var device = DateTimeOffset.Parse(e.GetProperty("device_time").GetString()!, CultureInfo.InvariantCulture);
        var received = DateTimeOffset.Parse(e.GetProperty("received_at").GetString()!, CultureInfo.InvariantCulture);
        var corrected = DateTimeOffset.Parse(e.GetProperty("corrected_time").GetString()!, CultureInfo.InvariantCulture);
        Check("E3b", Math.Abs((received - device).TotalSeconds - 600) < 3 && Math.Abs((corrected - received).TotalSeconds) < 4,
            $"이중 기록: device_time={device:o}(원본, 10 분 뒤) · received_at={received:o} · corrected_time={corrected:o} (보정 − 수신 = {(corrected - received).TotalMilliseconds:F0} ms)");
    }

    // --- 증거 4: 규모 — enqueue · 재생 · 복원 시간 (E-6 의 SQLite 전환 임계 근거) ---------------------------------

    private static async Task E4_Scale(EngineProcess engine, HttpClient admin, string data, string work, int scale)
    {
        var dir = Path.Combine(work, "e4-outbox");
        var payload = JsonSerializer.SerializeToElement(new { count = 7, label = "helmet", conf = 0.91 });
        var at = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        using (var box = AgentOutbox.Open(dir, maxBytes: long.MaxValue))
        {
            for (var i = 0; i < scale; i++) box.EnqueueEvent("line1", "count_changed", at, payload);
        }
        var enqueue = sw.Elapsed;
        var bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();

        sw.Restart();
        var box2 = AgentOutbox.Open(dir, maxBytes: long.MaxValue);          // 재시작 복원
        var restore = sw.Elapsed;
        var depth = box2.Stats().Depth;

        sw.Restart();
        var drained = 0;
        while (true)
        {
            var batch = box2.Peek(500);
            if (batch.Count == 0) break;
            foreach (var item in batch) { box2.Ack(item); drained++; }
        }
        var replay = sw.Elapsed;
        box2.Dispose();

        Console.WriteLine($"[E4] {scale} 건 · 항목당 파일 1 개 · outbox {bytes / 1024.0 / 1024:F1} MiB(실제 내용) / 파일 {files:N0}개");
        Console.WriteLine($"[E4]   enqueue  {enqueue.TotalSeconds,7:F1} s  ({scale / enqueue.TotalSeconds,8:F0} 건/s, fsync 포함)");
        Console.WriteLine($"[E4]   복원      {restore.TotalSeconds,7:F1} s  (디스크를 훑어 큐 순서 · 번호 복원, depth={depth})");
        Console.WriteLine($"[E4]   재생(ack) {replay.TotalSeconds,7:F1} s  ({drained / replay.TotalSeconds,8:F0} 건/s, 500건씩 Peek 후 Ack — 네트워크 제외)");
        Check("E4a", depth == scale && drained == scale, $"{scale} 건이 재시작 뒤 모두 복원되고 모두 재생됨(손실 0)");

        // 엔진까지 가는 시간은 규모를 줄여 잰다(엔진도 건당 파일을 쓴다) — 선형 외삽은 하지 않고 잰 값만 적는다.
        var deliverN = Math.Min(scale, 5000);
        var node = await RegisterNodeAsync(admin, engine.Url, work, "e4");
        using var http = new HttpClient();
        using var box3 = AgentOutbox.Open(Path.Combine(work, "e4b-outbox"));
        for (var i = 0; i < deliverN; i++) box3.EnqueueEvent("line1", "count_changed", at, payload);
        var agent = new EdgeAgent(new EngineClient(http, engine.Url + "/api/u-vision"), box3, node.StateDir, new AgentOptions { BatchSize = 500 });
        sw.Restart();
        var report = await agent.RunOnceAsync(CancellationToken.None);
        Console.WriteLine($"[E4]   엔진 전달 {sw.Elapsed.TotalSeconds,5:F1} s  ({deliverN / sw.Elapsed.TotalSeconds,8:F0} 건/s, 배치 500, 엔진도 건당 파일 2개 + 장부 한 줄을 씀) — {deliverN} 건만 잰 값");
        Check("E4b", report.Sent == deliverN && EngineEvents(data, node.NodeId).Count == deliverN, $"엔진에 {deliverN} 건 전달 · 수신 {EngineEvents(data, node.NodeId).Count}건");
    }

    // --- 증거 5: 일회용 토큰 재사용 거부 · 회수 뒤 401 -----------------------------------------------------------

    private static async Task E5_TokenAndRevoke(EngineProcess engine, HttpClient admin, string work, Registered revokedNode)
    {
        using var http = new HttpClient();
        var token = (await (await admin.PostAsJsonAsync("/api/u-vision/edge/tokens", new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var first = await http.PostAsJsonAsync($"{engine.Url}/api/u-vision/edge/v1/register", new { token });
        var again = await http.PostAsJsonAsync($"{engine.Url}/api/u-vision/edge/v1/register", new { token });
        Check("E5a", first.StatusCode == HttpStatusCode.OK && again.StatusCode == HttpStatusCode.Unauthorized, $"일회용 토큰: 첫 등록 {(int)first.StatusCode} · 재사용 {(int)again.StatusCode}");

        var bearer = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{revokedNode.NodeId}.{revokedNode.Secret}");
        async Task<HttpStatusCode> WhoAmI() { using var r = new HttpRequestMessage(HttpMethod.Get, $"{engine.Url}/api/u-vision/edge/v1/whoami"); r.Headers.Authorization = bearer; return (await http.SendAsync(r)).StatusCode; }
        var before = await WhoAmI();
        await admin.DeleteAsync($"/api/u-vision/edge/nodes/{revokedNode.NodeId}");
        var after = await WhoAmI();
        Check("E5b", before == HttpStatusCode.OK && after == HttpStatusCode.Unauthorized, $"노드 회수: 회수 전 {(int)before} · 회수 뒤 {(int)after}");

        using var box = AgentOutbox.Open(revokedNode.OutboxDir);
        var agent = new EdgeAgent(new EngineClient(http, engine.Url + "/api/u-vision"), box, revokedNode.StateDir);
        var report = await agent.RunOnceAsync(CancellationToken.None);
        Check("E5c", report.Revoked && agent.Revoked, "회수된 노드의 에이전트는 자격 거절을 받아 멈춤(큐는 지우지 않음)");
    }

    // --- 증거 6: 별도 프로세스의 에이전트가 보고한 상태가 엔진의 노드 목록에 보인다 -------------------------------

    private static async Task E6_StatusReport(EngineProcess engine, HttpClient admin, string data, string work)
    {
        var node = await RegisterNodeAsync(admin, engine.Url, work, "e6");
        // 정상 사건 20건 + 배정에 없는 시나리오의 사건 1건 — 엔진이 뒤의 것을 영구 거절해 에이전트가 «따로 둔 항목» 으로 센다.
        var replay = Path.Combine(work, "e6.jsonl");
        File.WriteAllLines(replay, Enumerable.Range(0, 20)
            .Select(i => JsonSerializer.Serialize(new { scenario_id = "line1", kind = "count_changed", data = new { n = i } }))
            .Append(JsonSerializer.Serialize(new { scenario_id = "not-assigned", kind = "count_changed", data = new { n = 99 } })));

        var agent = StartAgent(engine.Url, node, replay, start: 0, delayMs: 1);
        JsonElement? status = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var nodes = await admin.GetFromJsonAsync<JsonElement>("/api/u-vision/edge/nodes");
            var mine = nodes.EnumerateArray().FirstOrDefault(n => n.GetProperty("node_id").GetString() == node.NodeId);
            if (mine.ValueKind == JsonValueKind.Object && mine.TryGetProperty("status", out var s)
                && s.GetProperty("outbox_depth").GetInt32() == 0 && s.GetProperty("parked").GetInt32() == 1)
            {
                status = s.Clone();
                break;
            }
            await Task.Delay(100);
        }
        if (!agent.HasExited) agent.Kill(entireProcessTree: true);

        var delivered = EngineEvents(data, node.NodeId).Count;
        Check("E6a", status is not null,
            status is { } s2
                ? $"에이전트 프로세스의 보고가 노드 목록에 보임: 대기 {s2.GetProperty("outbox_depth").GetInt32()} · 따로 둔 {s2.GetProperty("parked").GetInt32()} · 버린 크롭 {s2.GetProperty("dropped_crops").GetInt64()} · 판 {s2.GetProperty("agent_version").GetString()}"
                : "60초 안에 «대기 0 · 따로 둔 1» 보고가 노드 목록에 나타나지 않음");
        Check("E6b", status is { } s3 && s3.GetProperty("agent_version").GetString() == EdgeAgent.AgentVersion,
            $"보고된 에이전트 판 = 라이브러리의 판({EdgeAgent.AgentVersion})");
        Check("E6c", delivered == 20, $"사건은 정상 20건만 엔진에 도착(배정 밖 1건은 따로 둠) — 도착 {delivered}건");
    }
}
