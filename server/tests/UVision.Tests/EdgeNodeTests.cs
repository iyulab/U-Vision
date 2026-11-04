using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 엣지 노드 신원(D-102 E-1) — 일회용 등록 토큰 → 노드별 비밀(Bearer). 토큰 · 비밀은 해시로만 저장하고, 한 번 쓴 · 만료된 · 틀린
/// 토큰은 구분 없이 거절하며, 회수한 노드는 곧바로 닫히고, 노드 자격으로는 관리 경로를 열 수 없다.
/// </summary>
public class EdgeNodeTests
{
    private const string Base = "/api/u-vision/edge";

    private static HttpClient Admin(PinFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        return c;
    }

    private static async Task<string> IssueAsync(HttpClient admin, object? body = null)
    {
        var resp = await admin.PostAsJsonAsync($"{Base}/tokens", body ?? new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static async Task<(string NodeId, string Secret)> RegisterAsync(HttpClient c, string token, string? name = null)
    {
        var resp = await c.PostAsJsonAsync($"{Base}/v1/register", new { token, name });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("node_id").GetString()!, body.GetProperty("secret").GetString()!);
    }

    private static Task<HttpResponseMessage> WhoAmIAsync(HttpClient c, string credential)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/v1/whoami");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return c.SendAsync(req);
    }

    [Fact]
    public async Task AdminEndpoints_NeedThePin()
    {
        using var f = new PinFactory();
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync($"{Base}/tokens", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"{Base}/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync($"{Base}/nodes/node_x")).StatusCode);
    }

    [Fact]
    public async Task RegisterWithToken_GivesAnIdentity_ThatAuthenticates()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var token = await IssueAsync(admin, new { name = "line-3 jetson" });

        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), token);

        var who = await WhoAmIAsync(f.CreateClient(), $"{nodeId}.{secret}");
        Assert.Equal(HttpStatusCode.OK, who.StatusCode);
        var node = await who.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(nodeId, node.GetProperty("node_id").GetString());
        // 토큰 발급 때의 이름이 기본 표시 이름이다.
        Assert.Equal("line-3 jetson", node.GetProperty("name").GetString());
        Assert.False(node.TryGetProperty("secret", out _));
        Assert.False(node.TryGetProperty("secret_hash", out _));
    }

    [Fact]
    public async Task ATokenWorksOnce_AndTheRejectionDoesNotSaySwhy()
    {
        using var f = new PinFactory();
        var token = await IssueAsync(Admin(f));
        await RegisterAsync(f.CreateClient(), token);

        var again = await f.CreateClient().PostAsJsonAsync($"{Base}/v1/register", new { token });
        var wrong = await f.CreateClient().PostAsJsonAsync($"{Base}/v1/register", new { token = "0123456789abcdef.not-a-real-secret-at-all-xx" });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        // 이미 쓴 토큰과 틀린 토큰의 응답이 같다 — 존재를 탐색하게 두지 않는다.
        Assert.Equal(await again.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());
    }

    /// <summary>토큰 파일의 만료 시각만 바꿔 쓴다(나머지 필드는 그대로).</summary>
    private static void SetExpiry(PinFactory f, string token, string expiresAt)
    {
        var tokenFile = Path.Combine(f.DataPath, "edge", "tokens", token.Split('.')[0] + ".json");
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(tokenFile))!;
        var changed = doc.ToDictionary(kv => kv.Key, kv => kv.Key == "expires_at" ? (object)expiresAt : kv.Value);
        File.WriteAllText(tokenFile, JsonSerializer.Serialize(changed));
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused_ButTheSameEditWithAFutureExpiryWorks()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var expired = await IssueAsync(admin, new { ttl_minutes = 1 });
        var control = await IssueAsync(admin, new { ttl_minutes = 1 });
        SetExpiry(f, expired, "2000-01-01T00:00:00.0000000+00:00");
        // 대조군: 같은 방식으로 파일을 다시 썼지만 만료가 미래 — 재작성이 파일을 깨뜨렸다면 이쪽도 거절돼 시험이 틀렸음을 드러낸다.
        SetExpiry(f, control, "2999-01-01T00:00:00.0000000+00:00");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await f.CreateClient().PostAsJsonAsync($"{Base}/v1/register", new { token = expired })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await f.CreateClient().PostAsJsonAsync($"{Base}/v1/register", new { token = control })).StatusCode);
    }

    [Fact]
    public async Task Secrets_AreNeverStoredInPlainText_NorListed()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var token = await IssueAsync(admin);
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), token, "n1");

        // 디스크의 어떤 파일에도 평문 비밀 · 평문 토큰이 없다.
        var plainParts = new[] { secret, token.Split('.')[1] };
        foreach (var file in Directory.GetFiles(Path.Combine(f.DataPath, "edge"), "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.All(plainParts, p => Assert.DoesNotContain(p, text));
        }
        var list = await (await admin.GetAsync($"{Base}/nodes")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, list);
        Assert.DoesNotContain("hash", list);
        Assert.Contains(nodeId, list);
    }

    [Fact]
    public async Task WrongSecret_UnknownNode_AndMissingHeader_Are401()
    {
        using var f = new PinFactory();
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(Admin(f)));
        var c = f.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(c, $"{nodeId}.{secret}x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(c, $"node_0000000000000000.{secret}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(c, "garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync($"{Base}/v1/whoami")).StatusCode);
    }

    [Fact]
    public async Task Revoking_ClosesThatNodeAtOnce_AndLeavesOthersAlone()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var a = await RegisterAsync(f.CreateClient(), await IssueAsync(admin), "a");
        var b = await RegisterAsync(f.CreateClient(), await IssueAsync(admin), "b");

        var revoked = await admin.DeleteAsync($"{Base}/nodes/{a.NodeId}");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.True((await revoked.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("revoked_at", out _));

        Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmIAsync(f.CreateClient(), $"{a.NodeId}.{a.Secret}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WhoAmIAsync(f.CreateClient(), $"{b.NodeId}.{b.Secret}")).StatusCode);
        // 기록은 남는다 — 목록에 회수 표시와 함께.
        var nodes = (await admin.GetFromJsonAsync<JsonElement>($"{Base}/nodes")).EnumerateArray().ToList();
        Assert.Equal(2, nodes.Count);
        Assert.True(nodes.Single(n => n.GetProperty("node_id").GetString() == a.NodeId).TryGetProperty("revoked_at", out _));
        // 두 번 회수해도 같다 · 모르는 노드는 404 · 형식 위반은 400.
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"{Base}/nodes/{a.NodeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{Base}/nodes/node_ffffffffffffffff")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"{Base}/nodes/bad%20id")).StatusCode);
    }

    [Fact]
    public async Task ANodeCredential_DoesNotOpenAdminEndpoints()
    {
        using var f = new PinFactory();
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(Admin(f)));
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{nodeId}.{secret}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync($"{Base}/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync($"{Base}/tokens", new { })).StatusCode);
    }

    [Fact]
    public async Task BadInput_Is400()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Base}/tokens", new { ttl_minutes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Base}/tokens", new { ttl_minutes = 99999 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{Base}/tokens", new { name = new string('x', 81) })).StatusCode);
    }

    [Fact]
    public async Task EdgeIsAReservedName_NotUsableAsAScenarioId()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var resp = await admin.PostAsJsonAsync("/api/u-vision/scenarios",
            new { scenario_id = "Edge", name = "x", criteria = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // 노드 저장소가 만들어졌어도 시나리오 목록에는 시나리오로 보이지 않는다.
        await IssueAsync(admin);
        var scenarios = await f.CreateClient().GetFromJsonAsync<JsonElement>("/api/u-vision/scenarios");
        Assert.DoesNotContain(scenarios.EnumerateArray(), s => s.GetProperty("scenario_id").GetString() == "edge");
    }

    // --- 에이전트 자기 상태 보고 ---------------------------------------------------------------

    private static HttpClient AsNode(PinFactory f, string nodeId, string secret)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{nodeId}.{secret}");
        return c;
    }

    private static object Report(int depth = 3, long dropped = 2, int parked = 1, string? version = "0.7.0") =>
        new { outbox_depth = depth, outbox_bytes = 4096L, dropped_crops = dropped, parked, agent_version = version };

    [Fact]
    public async Task StatusReport_NeedsANodeCredential_AdminPinIsNotEnough()
    {
        using var f = new PinFactory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PostAsJsonAsync($"{Base}/v1/status", Report())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Admin(f).PostAsJsonAsync($"{Base}/v1/status", Report())).StatusCode);
    }

    [Fact]
    public async Task StatusReport_IsShownInTheNodeList_AndWhoAmI_AsTheLatestOneOnly()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(admin));
        var node = AsNode(f, nodeId, secret);

        Assert.Equal(HttpStatusCode.NoContent, (await node.PostAsJsonAsync($"{Base}/v1/status", Report(depth: 3))).StatusCode);
        var first = (await admin.GetFromJsonAsync<JsonElement>($"{Base}/nodes")).EnumerateArray().Single().GetProperty("status");
        Assert.Equal(3, first.GetProperty("outbox_depth").GetInt32());
        Assert.Equal(4096, first.GetProperty("outbox_bytes").GetInt64());
        Assert.Equal(2, first.GetProperty("dropped_crops").GetInt64());
        Assert.Equal(1, first.GetProperty("parked").GetInt32());
        Assert.Equal("0.7.0", first.GetProperty("agent_version").GetString());

        // 값이 바뀌면 곧바로 덮어쓴다(마지막 하나만).
        Assert.Equal(HttpStatusCode.NoContent, (await node.PostAsJsonAsync($"{Base}/v1/status", Report(depth: 0, dropped: 9))).StatusCode);
        var who = await (await WhoAmIAsync(node, $"{nodeId}.{secret}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, who.GetProperty("status").GetProperty("outbox_depth").GetInt32());
        Assert.Equal(9, who.GetProperty("status").GetProperty("dropped_crops").GetInt64());
    }

    [Fact]
    public async Task StatusReport_WithTheSameValues_IsNotRewrittenWithinTheTouchInterval()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(admin));
        var node = AsNode(f, nodeId, secret);
        await node.PostAsJsonAsync($"{Base}/v1/status", Report());
        var path = Path.Combine(f.DataPath, "edge", "nodes", nodeId, "node.json");
        var content = File.ReadAllText(path);

        Assert.Equal(HttpStatusCode.NoContent, (await node.PostAsJsonAsync($"{Base}/v1/status", Report())).StatusCode);

        Assert.Equal(content, File.ReadAllText(path));                                  // 같은 값 + 1분 안 → 디스크에 안 쓴다(보고 시각도 그대로)
    }

    [Fact]
    public async Task StatusReport_BadValues_Are400()
    {
        using var f = new PinFactory();
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(Admin(f)));
        var node = AsNode(f, nodeId, secret);
        Assert.Equal(HttpStatusCode.BadRequest, (await node.PostAsJsonAsync($"{Base}/v1/status", Report(depth: -1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await node.PostAsJsonAsync($"{Base}/v1/status", Report(dropped: -5))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await node.PostAsJsonAsync($"{Base}/v1/status", Report(version: new string('v', 41)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await node.PostAsJsonAsync($"{Base}/v1/status", Report(version: "a\nb"))).StatusCode);
    }

    [Fact]
    public async Task StatusReport_AfterRevocation_IsRefused()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(admin));
        await admin.DeleteAsync($"{Base}/nodes/{nodeId}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await AsNode(f, nodeId, secret).PostAsJsonAsync($"{Base}/v1/status", Report())).StatusCode);
    }

    // --- 추론 워커 수신 상태(상태 보고의 worker) ---

    private static object WorkerReport(
        bool connected = true, long frames = 120, long dropped = 0, string? reason = null, int rules = 2, int skippedRules = 0,
        string? lastMessageAt = "2026-11-04T09:00:00.000+00:00", string[]? skipped = null) =>
        new
        {
            outbox_depth = 0, outbox_bytes = 0L, dropped_crops = 0L, parked = 0, agent_version = "0.7.0",
            worker = new { connected, last_message_at = lastMessageAt, frames, dropped, last_drop_reason = reason, rules, skipped_rules = skippedRules, skipped },
        };

    [Fact]
    public async Task StatusReport_WorkerState_IsShownInTheNodeList()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(admin));
        var node = AsNode(f, nodeId, secret);

        Assert.Equal(HttpStatusCode.NoContent, (await node.PostAsJsonAsync($"{Base}/v1/status",
            WorkerReport(dropped: 3, reason: "JSON 이 아니거나 모양이 맞지 않는다", skippedRules: 1, skipped: ["wh/ghost: 소스 nope 가 없다"]))).StatusCode);

        var w = (await admin.GetFromJsonAsync<JsonElement>($"{Base}/nodes")).EnumerateArray().Single().GetProperty("status").GetProperty("worker");
        Assert.True(w.GetProperty("connected").GetBoolean());
        Assert.Equal((120L, 3L, 2, 1), (w.GetProperty("frames").GetInt64(), w.GetProperty("dropped").GetInt64(), w.GetProperty("rules").GetInt32(), w.GetProperty("skipped_rules").GetInt32()));
        Assert.Equal("JSON 이 아니거나 모양이 맞지 않는다", w.GetProperty("last_drop_reason").GetString());
        Assert.Equal("wh/ghost: 소스 nope 가 없다", w.GetProperty("skipped")[0].GetString());
        Assert.StartsWith("2026-11-04T09:00:00", w.GetProperty("last_message_at").GetString());
    }

    [Fact]
    public async Task StatusReport_WithoutAWorker_HasNoWorkerObject()
    {
        using var f = new PinFactory();
        var admin = Admin(f);
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(admin));
        await AsNode(f, nodeId, secret).PostAsJsonAsync($"{Base}/v1/status", Report());

        var status = (await admin.GetFromJsonAsync<JsonElement>($"{Base}/nodes")).EnumerateArray().Single().GetProperty("status");

        Assert.False(status.TryGetProperty("worker", out _));
    }

    [Fact]
    public async Task StatusReport_OnlyTheCountersAndTheLastMessageChange_IsNotRewrittenWithinTheTouchInterval()
    {
        using var f = new PinFactory();
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(Admin(f)));
        var node = AsNode(f, nodeId, secret);
        await node.PostAsJsonAsync($"{Base}/v1/status", WorkerReport(frames: 100, lastMessageAt: "2026-11-04T09:00:00+00:00"));
        var path = Path.Combine(f.DataPath, "edge", "nodes", nodeId, "node.json");
        var content = File.ReadAllText(path);

        // 프레임 수와 마지막 메시지 시각은 보고마다 달라진다 — 그것만 다르면 30 초 폴링마다 노드 파일을 다시 쓰지 않는다.
        await node.PostAsJsonAsync($"{Base}/v1/status", WorkerReport(frames: 400, lastMessageAt: "2026-11-04T09:00:30+00:00"));
        Assert.Equal(content, File.ReadAllText(path));

        // 운영자가 봐야 하는 것(연결 · 버린 줄)이 바뀌면 곧바로 쓴다.
        await node.PostAsJsonAsync($"{Base}/v1/status", WorkerReport(connected: false, frames: 400));
        Assert.NotEqual(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task StatusReport_BadWorkerValues_Are400()
    {
        using var f = new PinFactory();
        var (nodeId, secret) = await RegisterAsync(f.CreateClient(), await IssueAsync(Admin(f)));
        var node = AsNode(f, nodeId, secret);
        async Task<HttpStatusCode> Post(object body) => (await node.PostAsJsonAsync($"{Base}/v1/status", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Post(WorkerReport(frames: -1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(WorkerReport(dropped: -1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(WorkerReport(rules: -1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(WorkerReport(reason: new string('r', 201))));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(WorkerReport(reason: "a\nb")));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(WorkerReport(lastMessageAt: "2026-11-04T09:00:00")));    // 시간대 없음
        Assert.Equal(HttpStatusCode.BadRequest, await Post(WorkerReport(lastMessageAt: "yesterday")));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(WorkerReport(skipped: [.. Enumerable.Range(0, 11).Select(i => $"r{i}")])));
        Assert.Equal(HttpStatusCode.NoContent, await Post(WorkerReport(lastMessageAt: null)));                       // 한 번도 메시지가 없었다
    }
}
