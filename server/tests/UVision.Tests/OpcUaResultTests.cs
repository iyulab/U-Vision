using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.Discovery;
using Opc.Ua.Client.Subscriptions;
using UVision.Api;
using UVision.Api.Models;
using UVision.Api.OpcUa;
using UVision.Api.Services.Events;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 프로세스 안 OPC UA 서버 하나 + 실제 OPC UA 프로토콜(클라이언트 스택)로 닿는 클라이언트 도우미.
/// 같은 구현 안의 왕복이라 «계약 시험» 이다 — 독립 구현과의 상호운용 증거가 아니다.
/// </summary>
public class OpcUaServerFixture : IAsyncLifetime
{
    private Task<OpcUaTestClient>? _trusted;

    /// <summary>최근 결과 창의 크기 — 창 상한 시험은 작은 창의 서버를 따로 세운다.</summary>
    protected virtual int ResultWindow => 50;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "uvision-opcua-results-" + Guid.NewGuid().ToString("N"));
    public int Port { get; private set; }
    public IHost Host { get; private set; } = null!;
    public OpcUaResultHub Hub => Host.Services.GetRequiredService<OpcUaResultHub>();
    public string ServerPki => Path.Combine(Root, "opcua", "pki");
    public string EndpointUrl => $"opc.tcp://localhost:{Port}/UVision";

    public OpcUaTransport Transport =>
        (OpcUaTransport)Host.Services.GetRequiredService<IEnumerable<IEventDestinationSource>>()
            .Single().Destinations.Single().Transport;

    public async ValueTask InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UVision:Storage:DataPath"] = Root,
            ["UVision:Events:OpcUa:Enabled"] = "true",
            ["UVision:Events:OpcUa:Port"] = Port.ToString(),
            ["UVision:Events:OpcUa:Host"] = "localhost",
            ["UVision:Events:OpcUa:ResultWindow"] = ResultWindow.ToString(),
        });
        builder.Logging.ClearProviders();
        builder.Services.AddUVisionOpcUa(builder.Configuration, Root);
        Host = builder.Build();
        await Host.StartAsync();
        await Hub.WaitUntilRunningAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
    }

    /// <summary>
    /// 신뢰된 클라이언트 하나(시험들이 공유) — 서버 신뢰 절차를 그대로 밟아 만든다: 신뢰 목록에 없는 인증서로 접속 시도 →
    /// 서버가 거절 폴더에 둔 인증서를 신뢰 폴더로 옮김 → 같은 클라이언트가 다시 접속.
    /// </summary>
    public Task<OpcUaTestClient> TrustedClientAsync() => _trusted ??= OpcUaTrust.ConnectTrustedAsync(this, "shared");

    public async ValueTask DisposeAsync()
    {
        if (_trusted is { IsCompletedSuccessfully: true })
            await _trusted.Result.DisposeAsync();
        try { await Host.StopAsync(); } catch (Exception) { /* 시험 정리 */ }
        Host.Dispose();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* 서버가 막 내려가는 중 */ }
    }
}


/// <summary>서버 인증서 신뢰 절차 — 거절 → 신뢰 폴더로 옮김 → 접속.</summary>
public static class OpcUaTrust
{
    /// <summary>
    /// 서버가 신뢰하지 않는 클라이언트 인증서로 접속을 시도하고(거절되어야 한다), 서버가 <c>rejected/certs</c> 에 둔 그
    /// 인증서를 <c>trusted/certs</c> 로 복사한 뒤 같은 클라이언트로 다시 접속한다. 폴더 배치는 스택이 정하고 첫 접속 시도에서
    /// 생기므로(관찰한 배치는 시험 이름 <c>Untrusted_Client_Is_Rejected_Then_Connects_After_Its_Certificate_Is_Trusted</c> 가 확인한다)
    /// 거절 폴더는 시도 뒤에 찾는다.
    /// </summary>
    public static async Task<OpcUaTestClient> ConnectTrustedAsync(OpcUaServerFixture server, string name)
    {
        var pki = Path.Combine(server.Root, "client-pki-" + name);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            OpcUaTestClient.ConnectAsync(pki, server.EndpointUrl, name, TimeSpan.FromSeconds(8)));
        TrustRejected(server, name);
        // 서버가 신뢰 목록을 캐시할 수 있어 짧게 간격을 두고 몇 번 다시 시도한다.
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return await OpcUaTestClient.ConnectAsync(pki, server.EndpointUrl, name, TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        throw new InvalidOperationException("trusted client could not connect", last);
    }

    /// <summary>거절 폴더의 해당 클라이언트 인증서(RSA — 접속에 쓰이는 것)를 신뢰 폴더로 복사한다.</summary>
    public static void TrustRejected(OpcUaServerFixture server, string name)
    {
        var rejected = Path.Combine(server.ServerPki, "rejected", "certs");
        var applicationName = "UVisionTestClient-" + name;
        var cert = Directory.EnumerateFiles(rejected, applicationName + " *.der").ToArray();
        Assert.NotEmpty(cert);
        var trusted = Path.Combine(server.ServerPki, "trusted", "certs");
        Directory.CreateDirectory(trusted);
        foreach (var file in cert)
            File.Copy(file, Path.Combine(trusted, Path.GetFileName(file)), overwrite: true);
    }
}

/// <summary>클라이언트 스택(OPC Foundation)으로 서버에 닿는 시험용 클라이언트 — 자기 PKI 폴더와 응용 인증서를 따로 쥔다.</summary>
public sealed class OpcUaTestClient : IAsyncDisposable
{
    private readonly IHost _host;
    public ManagedSession Session { get; }

    private OpcUaTestClient(IHost host, ManagedSession session)
    {
        _host = host;
        Session = session;
    }

    /// <summary>
    /// 연결한다. 클라이언트는 서버 인증서를 자동 수락한다(서버 쪽 신뢰 절차를 시험하는 것이지 클라이언트 쪽이 아니다).
    /// 서버가 이 클라이언트의 인증서를 신뢰하지 않으면 제한 시간 안에 접속되지 않아 예외가 난다.
    /// </summary>
    public static async Task<OpcUaTestClient> ConnectAsync(string pki, string url, string name, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var host = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        host.Logging.ClearProviders();
        host.Services
            .AddOpcUa()
            .AddClient(o =>
            {
                o.ApplicationName = "UVisionTestClient-" + name;
                o.ApplicationUri = "urn:localhost:UVision:TestClient:" + name;
                o.ProductUri = "uri:u-vision:test-client";
                o.PkiRoot = pki;
                o.AutoAcceptUntrustedCertificates = true;
                o.RejectSHA1SignedCertificates = true;
                o.MinimumCertificateKeySize = 2048;
                o.Session = new ManagedSessionOptions { SessionName = "UVisionTestClient-" + name, SessionTimeout = TimeSpan.FromSeconds(60) };
            })
            .AddDiscoveryAndConnect(o =>
            {
                o.DiscoveryUrl = url;
                o.SecurityMode = MessageSecurityMode.SignAndEncrypt;
                o.SecurityPolicyUri = SecurityPolicies.Basic256Sha256;
            })
            .AddSubscriptions();
        var built = host.Build();
        try
        {
            await built.StartAsync(cts.Token);
            var connect = built.Services.GetRequiredService<Func<CancellationToken, Task<ManagedSession>>>();
            return new OpcUaTestClient(built, await connect(cts.Token));
        }
        catch
        {
            try { await built.StopAsync(CancellationToken.None); } catch (Exception) { /* 정리 */ }
            built.Dispose();
            throw;
        }
    }

    /// <summary>서버가 내놓는 종단 목록(보안 모드 · 정책).</summary>
    public async Task<EndpointDescription[]> GetEndpointsAsync(string url) =>
        (await _host.Services.GetRequiredService<IOpcUaDiscoveryService>()
            .GetEndpointsAsync(url, default, CancellationToken.None)).ToArray()!;

    public ushort NamespaceIndex => (ushort)Session.NamespaceUris.GetIndex(OpcUaResultNames.NamespaceUri);

    public NodeId ManagementId => new(OpcUaResultNames.ResultManagement, NamespaceIndex);
    public NodeId EventTypeId => new(OpcUaResultNames.EventType, NamespaceIndex);

    /// <summary>결과 관리 메서드를 부른다. 호출 자체의 상태가 나쁘면 예외 — 출력 인자를 돌려준다.</summary>
    public async Task<Variant[]> CallAsync(string method, params Variant[] args)
    {
        var request = new CallMethodRequest
        {
            ObjectId = ManagementId,
            MethodId = new NodeId(OpcUaResultNames.ResultManagement + "." + method, NamespaceIndex),
            InputArguments = args,
        };
        var response = await Session.CallAsync(null!, new[] { request }, CancellationToken.None);
        var result = response.Results[0];
        Assert.True(StatusCode.IsGood(result.StatusCode), "call failed: " + result.StatusCode);
        return result.OutputArguments.ToArray()!;
    }

    /// <summary>선택 절: EventType 과 모든 결과 속성 — 순서는 <see cref="Fields"/>. 우리 유형만 오게 OfType 으로 거른다.</summary>
    public EventFilter ResultEventFilter(bool ofType = true)
    {
        var filter = new EventFilter();
        var select = new List<SimpleAttributeOperand>
        {
            new(ObjectTypeIds.BaseEventType, new QualifiedName(BrowseNames.EventType)) { AttributeId = Attributes.Value },
        };
        foreach (var d in ResultReadyEventState.Declarations)
            select.Add(new SimpleAttributeOperand(EventTypeId, new QualifiedName(d.Name, NamespaceIndex)) { AttributeId = Attributes.Value });
        filter.SelectClauses = select.ToArray();
        if (ofType)
            filter.WhereClause.Push(FilterOperator.OfType, new Variant(EventTypeId));
        return filter;
    }

    /// <summary><see cref="ResultEventFilter"/> 의 필드 이름 순서 — 0번은 EventType.</summary>
    public static IReadOnlyList<string> Fields { get; } =
        new[] { "EventType" }.Concat(ResultReadyEventState.Declarations.Select(d => d.Name)).ToArray();

    /// <summary>이벤트 구독을 시작해 도착 순서대로 필드를 모은 채널을 돌려준다(취소하면 구독이 끝난다).</summary>
    public Channel<ArrayOf<Variant>> SubscribeResults(NodeId notifier, EventFilter filter, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<ArrayOf<Variant>>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var n in Session.DefaultStreaming.SubscribeEventsAsync(notifier, filter, ct: cancellationToken))
                    channel.Writer.TryWrite(n.Fields);
            }
            catch (OperationCanceledException) { channel.Writer.TryComplete(); }
            catch (Exception ex) { channel.Writer.TryComplete(ex); }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);
        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Session.DisposeAsync(); } catch (Exception) { /* 정리 */ }
        try { await _host.StopAsync(CancellationToken.None); } catch (Exception) { /* 정리 */ }
        _host.Dispose();
    }
}

/// <summary>시험용 판정 사건 만들기 — 실제 발행기와 같이 <c>DecisionEvent.From</c> + 기본 옵션 직렬화.</summary>
internal static class OpcUaSamples
{
    public static InspectResultV2 Decision(
        string id, string scenario = "line-a", string cls = "OK", bool? review = null, int? count = null, string? findings = null) => new()
    {
        DecisionId = id,
        ScenarioId = scenario,
        Timestamp = "2026-11-03T04:05:06.1234567+00:00",
        Class = cls,
        SettledBy = new SettledBy { Tier = "vlm", Kind = "vlm" },
        Confidence = 0.93,
        Findings = findings,
        RequiresReview = review,
        Count = count is null ? null : new CountInfo
        {
            Total = count.Value, ByClass = new Dictionary<string, int> { ["bolt"] = count.Value },
            Expected = count.Value, Tolerance = 0, MinConfidence = 0.5, Uncertain = false, Boxes = [],
        },
    };

    public static OutboxEntry Entry(InspectResultV2 decision)
    {
        var evt = DecisionEvent.From(decision);
        return new OutboxEntry { EventId = evt.Id, Body = JsonSerializer.Serialize(evt, new JsonSerializerOptions()) };
    }

    public static void WriteScenario(string root, string scenario, string json)
    {
        var dir = Path.Combine(root, scenario);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "scenario.json"), json);
    }
}

public sealed class OpcUaResultTests(OpcUaServerFixture server, ITestOutputHelper output) : IClassFixture<OpcUaServerFixture>
{
    private static Task<string?> Send(OpcUaServerFixture server, InspectResultV2 d) =>
        server.Transport.SendAsync(d.ScenarioId, OpcUaSamples.Entry(d), DateTimeOffset.UtcNow, CancellationToken.None);

    private static string Str(Variant v) => v.TryGetValue(out string s) ? s : throw new InvalidOperationException("not a string: " + v);

    [Fact]
    public async Task Untrusted_Client_Is_Rejected_Then_Connects_After_Its_Certificate_Is_Trusted()
    {
        var name = "flow";
        var pki = Path.Combine(server.Root, "client-pki-" + name);

        // 1) 신뢰 목록에 없는 인증서 — 접속이 안 되고, 서버가 그 인증서를 거절 폴더에 둔다.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            OpcUaTestClient.ConnectAsync(pki, server.EndpointUrl, name, TimeSpan.FromSeconds(8)));
        var rejected = Directory.EnumerateFiles(Path.Combine(server.ServerPki, "rejected", "certs"), "UVisionTestClient-flow *.der").ToArray();
        Assert.NotEmpty(rejected);
        Assert.False(Directory.Exists(Path.Combine(server.ServerPki, "trusted", "certs"))
            && Directory.EnumerateFiles(Path.Combine(server.ServerPki, "trusted", "certs"), "UVisionTestClient-flow *.der").Any());
        foreach (var root in new[] { server.ServerPki, pki })
        {
            output.WriteLine("tree " + root);
            foreach (var f in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order())
                output.WriteLine("  " + Path.GetRelativePath(root, f));
        }

        // 2) 관리자가 하는 일 — 거절 폴더의 인증서를 신뢰 폴더로 옮긴다. 3) 같은 클라이언트가 서명+암호화 종단으로 접속된다.
        OpcUaTrust.TrustRejected(server, name);
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await using var client = await OpcUaTestClient.ConnectAsync(pki, server.EndpointUrl, name, TimeSpan.FromSeconds(15));
                Assert.Equal(MessageSecurityMode.SignAndEncrypt, client.Session.ConfiguredEndpoint!.Description.SecurityMode);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        throw new InvalidOperationException("trusted client could not connect", last);
    }

    /// <summary>구독을 만들고, 서버에 구독이 만들어질 때까지 워밍업 사건을 흘려 보낸다.</summary>
    private async Task<(Channel<ArrayOf<Variant>> Events, CancellationTokenSource Stop)> SubscribeReadyAsync(
        OpcUaTestClient client, NodeId notifier, string scenario)
    {
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = client.SubscribeResults(notifier, client.ResultEventFilter(), stop.Token);
        // 구독이 서버에 만들어졌다는 신호가 없어 — 첫 사건이 도착할 때까지 워밍업 사건(서로 다른 판정 id)을 보낸다.
        for (var i = 0; i < 80 && !events.Reader.TryPeek(out _); i++)
        {
            if (events.Reader.Completion.IsFaulted)
                throw new InvalidOperationException("event subscription failed", events.Reader.Completion.Exception!.GetBaseException());
            Assert.Null(await Send(server, OpcUaSamples.Decision("warmup-" + Guid.NewGuid().ToString("N"), scenario)));
            await Task.Delay(250);
        }
        Assert.True(events.Reader.TryPeek(out _), "no event ever arrived through the subscription");
        return (events, stop);
    }

    private static async Task<ArrayOf<Variant>> NextWithResultIdAsync(Channel<ArrayOf<Variant>> events, string resultId, CancellationToken ct)
    {
        var at = OpcUaTestClient.Fields.ToList().IndexOf("ResultId");
        while (await events.Reader.WaitToReadAsync(ct))
            while (events.Reader.TryRead(out var fields))
                if (Str(fields[at]) == resultId)
                    return fields;
        throw new InvalidOperationException("event " + resultId + " never arrived");
    }

    private static Variant Field(ArrayOf<Variant> fields, string name) => fields[OpcUaTestClient.Fields.ToList().IndexOf(name)];

    [Theory]
    [InlineData(false)] // ResultManagement 객체에 구독
    [InlineData(true)]  // 서버 객체(집계 알림자)에 구독
    public async Task Subscriber_Receives_The_Result_Ready_Event_With_The_Mandatory_Fields(bool onServerObject)
    {
        OpcUaSamples.WriteScenario(server.Root, "line-a", "{\"scenario_id\":\"line-a\",\"name\":\"A\",\"criteria\":\"no scratches\"}");
        var client = await server.TrustedClientAsync();
        var (events, stop) = await SubscribeReadyAsync(client, onServerObject ? ObjectIds.Server : client.ManagementId, "line-a");
        using var _ = stop;

        var id = "evt-" + Guid.NewGuid().ToString("N");
        var decision = OpcUaSamples.Decision(id, review: true, findings: "한글 소견 — non-ascii");
        Assert.Null(await Send(server, decision));
        var fields = await NextWithResultIdAsync(events, id, stop.Token);

        Assert.Equal(client.EventTypeId, fields[0].TryGetValue(out NodeId type) ? type : default);
        Assert.False(Field(fields, "IsPartial").TryGetValue(out bool partial) ? partial : true);
        Assert.Equal(1, Field(fields, "ResultState").TryGetValue(out int state) ? state : -1);
        Assert.Equal("line-a", Str(Field(fields, "InternalRecipeId")));
        Assert.Equal(id, Str(Field(fields, "JobId")));
        Assert.Equal(id, Str(Field(fields, "ResultId")));
        // 지문: 시나리오 정의의 정규화 JSON sha256 앞 12자(소문자 16진).
        var fingerprint = Str(Field(fields, "InternalConfigurationId"));
        Assert.Matches("^[0-9a-f]{12}$", fingerprint);
        Assert.Equal(ScenarioFacts.FromScenarioJson("{ \"criteria\": \"no scratches\", \"name\": \"A\", \"scenario_id\": \"line-a\" }").ConfigurationId, fingerprint);
        Assert.True(Field(fields, "CreationTime").TryGetValue(out DateTimeUtc created));
        Assert.Equal(DateTime.Parse("2026-11-03T04:05:06.1234567Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            ((DateTime)created).ToUniversalTime());
        Assert.Equal("OK", Str(Field(fields, "Verdict")));
        Assert.Equal("OK", Str(Field(fields, "Class")));
        Assert.Equal("vlm", Str(Field(fields, "SettledBy")));
        Assert.True(Field(fields, "RequiresReview").TryGetValue(out bool review) && review);
        Assert.True(Field(fields, "Count").IsNull);
        // json = 봉투 data 원문 그대로(재직렬화 없음 — 비 ASCII 가 이스케이프된 채로 byte 같다).
        var envelope = OpcUaSamples.Entry(decision).Body;
        using var doc = JsonDocument.Parse(envelope);
        Assert.Equal(doc.RootElement.GetProperty("data").GetRawText(), Str(Field(fields, "Json")));
    }

    [Fact]
    public async Task Ng_Verdict_Is_Exactly_NG_And_Counting_Fills_Count()
    {
        OpcUaSamples.WriteScenario(server.Root, "line-ng", "{\"scenario_id\":\"line-ng\"}");
        var client = await server.TrustedClientAsync();
        var (events, stop) = await SubscribeReadyAsync(client, client.ManagementId, "line-ng");
        using var _ = stop;

        var id = "ng-" + Guid.NewGuid().ToString("N");
        Assert.Null(await Send(server, OpcUaSamples.Decision(id, "line-ng", cls: "scratch", count: 7)));
        var fields = await NextWithResultIdAsync(events, id, stop.Token);
        Assert.Equal("NG", Str(Field(fields, "Verdict")));      // 불량 유형은 모두 NG — wire 와 같은 문자열
        Assert.Equal("scratch", Str(Field(fields, "Class")));
        Assert.Equal(7, Field(fields, "Count").TryGetValue(out int count) ? count : -1);
    }

    [Fact]
    public async Task GetResultById_Returns_The_Result_And_A_Not_Found_Error_For_An_Unknown_Id()
    {
        OpcUaSamples.WriteScenario(server.Root, "line-a", "{\"scenario_id\":\"line-a\"}");
        var client = await server.TrustedClientAsync();
        var id = "byid-" + Guid.NewGuid().ToString("N");
        var decision = OpcUaSamples.Decision(id, cls: "dent");
        Assert.Null(await Send(server, decision));

        var found = await client.CallAsync(OpcUaResultNames.GetResultById, id, 0);
        Assert.Equal(3, found.Length);                       // resultHandle, result, error
        Assert.True(found[0].TryGetValue(out uint handle) && handle > 0);
        Assert.Equal(0, found[2].TryGetValue(out int error) ? error : -99);
        using (var doc = JsonDocument.Parse(Str(found[1])))
        {
            var root = doc.RootElement;
            Assert.Equal(id, root.GetProperty("resultId").GetString());
            Assert.Equal(1, root.GetProperty("resultState").GetInt32());
            Assert.Equal("line-a", root.GetProperty("internalRecipeId").GetString());
            var content = root.GetProperty("resultContent")[0];
            Assert.Equal("NG", content.GetProperty("verdict").GetString());
            using var envelope = JsonDocument.Parse(OpcUaSamples.Entry(decision).Body);
            Assert.Equal(envelope.RootElement.GetProperty("data").GetRawText(), content.GetProperty("json").GetString());
        }

        var unknown = await client.CallAsync(OpcUaResultNames.GetResultById, "no-such-result", 0);
        Assert.Equal(OpcUaResultNames.ErrorNotFound, unknown[2].TryGetValue(out int notFound) ? notFound : 0);
        Assert.Equal("", Str(unknown[1]));

        var components = await client.CallAsync(OpcUaResultNames.GetResultComponentsById, id, 0);
        Assert.Equal(17, components.Length);                 // 표준의 출력 17개 — 이름 · 순서 그대로
        Assert.Equal(id, Str(components[12]));               // jobId
        Assert.Equal("line-a", Str(components[8]));          // internalRecipeId
        Assert.True(components[14].IsNull);                  // processingTimes — 싣지 않는다
        Assert.True(components[15].TryGetValue(out ArrayOf<string> contentList) && contentList.Count == 1);   // resultContent
        Assert.Equal(0, components[16].TryGetValue(out int componentsError) ? componentsError : -99);   // error
        var notFoundComponents = await client.CallAsync(OpcUaResultNames.GetResultComponentsById, "no-such-result", 0);
        Assert.Equal(OpcUaResultNames.ErrorNotFound, notFoundComponents[16].TryGetValue(out int componentsNotFound) ? componentsNotFound : 0);
    }

    [Fact]
    public async Task GetResultListFiltered_Filters_Pages_And_Rejects_A_Misaligned_StartIndex()
    {
        OpcUaSamples.WriteScenario(server.Root, "list-a", "{\"scenario_id\":\"list-a\"}");
        OpcUaSamples.WriteScenario(server.Root, "list-b", "{\"scenario_id\":\"list-b\"}");
        var client = await server.TrustedClientAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 5; i++)
            Assert.Null(await Send(server, OpcUaSamples.Decision($"la-{tag}-{i}", "list-a")));
        Assert.Null(await Send(server, OpcUaSamples.Decision($"lb-{tag}-0", "list-b", cls: "NG")));

        Variant[] Args(string recipe, string job, uint max, uint start) =>
            [0, "", "", "", recipe, "", "", "", job, max, start, 0];

        // 시나리오(레시피)로 거르고 두 쪽으로 나눠 받는다 — 도착 순서(오래된 것 먼저).
        var first = await client.CallAsync(OpcUaResultNames.GetResultListFiltered, Args("list-a", "", 3, 0));
        Assert.False(first[0].TryGetValue(out bool complete) && complete);   // isComplete
        Assert.Equal(3u, first[1].TryGetValue(out uint count) ? count : 0);
        Assert.Equal(0, first[4].TryGetValue(out int error) ? error : -99);
        Assert.True(first[3].TryGetValue(out ArrayOf<string> list));
        Assert.Equal(3, list.Count);
        var ids = new List<string>();
        foreach (var json in list.ToArray()!)
            using (var doc = JsonDocument.Parse(json)) ids.Add(doc.RootElement.GetProperty("resultId").GetString()!);
        Assert.Equal(new[] { $"la-{tag}-0", $"la-{tag}-1", $"la-{tag}-2" }, ids);

        var second = await client.CallAsync(OpcUaResultNames.GetResultListFiltered, Args("list-a", "", 3, 3));
        Assert.True(second[0].TryGetValue(out bool secondComplete) && secondComplete);
        Assert.Equal(2u, second[1].TryGetValue(out uint secondCount) ? secondCount : 0);

        // jobId 로 하나만.
        var byJob = await client.CallAsync(OpcUaResultNames.GetResultListFiltered, Args("", $"lb-{tag}-0", 0, 0));
        Assert.Equal(1u, byJob[1].TryGetValue(out uint jobCount) ? jobCount : 0);
        Assert.True(byJob[0].TryGetValue(out bool jobComplete) && jobComplete);

        // 엔진이 모르는 값(예: 측정 id)으로 걸면 맞는 결과가 없다.
        var byMeas = await client.CallAsync(OpcUaResultNames.GetResultListFiltered, [0, "meas-1", "", "", "list-a", "", "", "", "", 0u, 0u, 0]);
        Assert.Equal(0u, byMeas[1].TryGetValue(out uint measCount) ? measCount : 99);

        // 표준: startIndex 는 maxResults 의 배수여야 한다.
        var misaligned = await client.CallAsync(OpcUaResultNames.GetResultListFiltered, Args("list-a", "", 3, 2));
        Assert.Equal(OpcUaResultNames.ErrorInvalidArgument, misaligned[4].TryGetValue(out int misalignedError) ? misalignedError : 0);
    }

    [Fact]
    public async Task A_Redelivered_Event_Is_Not_Added_Or_Raised_Twice()
    {
        OpcUaSamples.WriteScenario(server.Root, "line-a", "{\"scenario_id\":\"line-a\"}");
        var client = await server.TrustedClientAsync();
        var (events, stop) = await SubscribeReadyAsync(client, client.ManagementId, "line-a");
        using var _ = stop;

        var id = "dup-" + Guid.NewGuid().ToString("N");
        Assert.Null(await Send(server, OpcUaSamples.Decision(id)));
        await NextWithResultIdAsync(events, id, stop.Token);
        Assert.Null(await Send(server, OpcUaSamples.Decision(id)));   // 최소 한 번 전달 — 다시 와도 성공이고 사건은 다시 올라가지 않는다
        // 뒤따르는 다른 사건 하나를 기다려, 그 사이에 같은 사건이 한 번 더 도착하지 않았음을 본다.
        var marker = "dup-marker-" + Guid.NewGuid().ToString("N");
        Assert.Null(await Send(server, OpcUaSamples.Decision(marker)));
        var at = OpcUaTestClient.Fields.ToList().IndexOf("ResultId");
        var seen = new List<string>();
        while (!seen.Contains(marker) && await events.Reader.WaitToReadAsync(stop.Token))
            while (events.Reader.TryRead(out var fields))
                seen.Add(Str(fields[at]));
        Assert.Contains(marker, seen);
        Assert.DoesNotContain(id, seen);
        Assert.Single(server.Hub.Window.List(new ResultFilter(JobId: id), 0, 0).Items);
    }

    [Fact]
    public async Task The_Real_Outbox_And_Dispatcher_Deliver_Into_The_Server_And_Clear_The_Outbox_Entry()
    {
        OpcUaSamples.WriteScenario(server.Root, "line-ob", "{\"scenario_id\":\"line-ob\"}");
        var paths = new UVision.Api.Storage.StoragePaths(new UVision.Api.Configuration.StorageOptions { DataPath = server.Root }, server.Root);
        var sources = server.Host.Services.GetServices<IEventDestinationSource>().ToArray();
        var options = new UVision.Api.Configuration.EventsOptions();
        var publisher = new DecisionEventPublisher(paths, options, new EventDeliverySignal(), sources);
        Assert.True(publisher.IsEnabled);

        var id = "ob-" + Guid.NewGuid().ToString("N");
        await publisher.PublishAsync(OpcUaSamples.Decision(id, "line-ob"), CancellationToken.None);
        var outbox = paths.EventOutboxDir("line-ob", OpcUaDestinationSource.DestinationName);
        Assert.Single(Directory.GetFiles(outbox, "*.json"));

        await using var transports = new EventTransports(options, new NoHttpClients(), sources);
        var dispatcher = new EventDispatcher(paths, options, transports,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EventDispatcher>.Instance);
        Assert.Equal(1, await dispatcher.DeliverDueAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(outbox, "*.json"));
        Assert.NotNull(server.Hub.Window.Find(id));
        Assert.NotNull(server.Hub.LastEventAt);
    }

    [Fact]
    public async Task Only_Signed_And_Encrypted_Endpoints_Are_Offered_By_Default()
    {
        var client = await server.TrustedClientAsync();
        var endpoints = await client.GetEndpointsAsync(server.EndpointUrl);
        Assert.NotEmpty(endpoints);
        foreach (var e in endpoints)
            output.WriteLine($"{e.SecurityMode} {e.SecurityPolicyUri} {e.EndpointUrl}");
        Assert.All(endpoints, e => Assert.Equal(MessageSecurityMode.SignAndEncrypt, e.SecurityMode));
        Assert.All(endpoints, e => Assert.Contains(e.UserIdentityTokens.ToArray()!, t => t.TokenType == UserTokenType.Anonymous));
    }

    [Fact]
    public async Task Malformed_Body_Returns_An_Error_Without_Throwing()
    {
        var error = await server.Transport.SendAsync("line-a",
            new OutboxEntry { EventId = "bad", Body = "{ not json" }, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(error);
        Assert.StartsWith("malformed event", error);
    }
}

internal sealed class NoHttpClients : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => throw new InvalidOperationException("no webhooks in this test");
}

/// <summary>작은 창(5)의 서버 — 창 상한을 프로토콜로 확인한다.</summary>
public sealed class SmallWindowFixture : OpcUaServerFixture
{
    protected override int ResultWindow => 5;
}

public sealed class SmallWindowOpcUaResultTests(SmallWindowFixture server) : IClassFixture<SmallWindowFixture>
{
    [Fact]
    public async Task Window_Drops_The_Oldest_Results_Beyond_Its_Size_As_Seen_Through_The_Protocol()
    {
        OpcUaSamples.WriteScenario(server.Root, "cap", "{\"scenario_id\":\"cap\"}");
        var client = await server.TrustedClientAsync();
        for (var i = 0; i < 8; i++)
            Assert.Null(await server.Transport.SendAsync("cap", OpcUaSamples.Entry(OpcUaSamples.Decision($"cap-{i}", "cap")),
                DateTimeOffset.UtcNow, CancellationToken.None));

        // 오래된 셋은 밀려나 창에서 조회되지 않고, 최근 다섯은 도착 순서로 남는다.
        var all = await client.CallAsync(OpcUaResultNames.GetResultListFiltered, [0, "", "", "", "cap", "", "", "", "", 0u, 0u, 0]);
        Assert.Equal(5u, all[1].TryGetValue(out uint count) ? count : 0);
        Assert.True(all[3].TryGetValue(out ArrayOf<string> list));
        var ids = list.ToArray()!.Select(j => JsonDocument.Parse(j).RootElement.GetProperty("resultId").GetString()).ToArray();
        Assert.Equal(new[] { "cap-3", "cap-4", "cap-5", "cap-6", "cap-7" }, ids);
        var dropped = await client.CallAsync(OpcUaResultNames.GetResultById, "cap-0", 0);
        Assert.Equal(OpcUaResultNames.ErrorNotFound, dropped[2].TryGetValue(out int error) ? error : 0);
    }
}

public sealed class WindowOpcUaResultTests
{
    private static OpcUaResult R(string id, string scenario = "s", int state = 1) => new(
        id, id, scenario, "cfg", false, state, DateTime.UtcNow, "OK", "OK", "vlm", "vlm", false, null, "{}");

    [Fact]
    public void Drops_The_Oldest_When_Full_And_Keeps_Arrival_Order()
    {
        var window = new OpcUaResultWindow(3);
        for (var i = 0; i < 5; i++)
            Assert.True(window.TryAdd(R("r" + i)));
        Assert.Equal(3, window.Count);
        Assert.Null(window.Find("r0"));
        Assert.Null(window.Find("r1"));
        Assert.Equal(new[] { "r2", "r3", "r4" }, window.List(new ResultFilter(), 0, 0).Items.Select(r => r.ResultId));
    }

    [Fact]
    public void A_Duplicate_ResultId_Is_Not_Added_Twice()
    {
        var window = new OpcUaResultWindow(10);
        Assert.True(window.TryAdd(R("a")));
        Assert.False(window.TryAdd(R("a")));
        Assert.Equal(1, window.Count);
    }

    [Fact]
    public void A_Result_Evicted_Then_Redelivered_Is_Added_Again()
    {
        var window = new OpcUaResultWindow(1);
        Assert.True(window.TryAdd(R("a")));
        Assert.True(window.TryAdd(R("b")));
        Assert.True(window.TryAdd(R("a")));
        Assert.Equal(1, window.Count);
    }

    [Fact]
    public void Paging_Reports_Completeness()
    {
        var window = new OpcUaResultWindow(10);
        for (var i = 0; i < 5; i++)
            window.TryAdd(R("r" + i));
        var first = window.List(new ResultFilter(), 0, 2);
        Assert.False(first.IsComplete);
        Assert.Equal(2, first.Items.Count);
        Assert.True(window.List(new ResultFilter(), 4, 2).IsComplete);
        Assert.True(window.List(new ResultFilter(), 0, 0).IsComplete);
    }

    [Fact]
    public void Unknown_Filter_Fields_Match_Nothing_And_State_Zero_Matches_All()
    {
        var window = new OpcUaResultWindow(10);
        window.TryAdd(R("a", state: 1));
        Assert.Single(window.List(new ResultFilter(ResultState: 0), 0, 0).Items);
        Assert.Single(window.List(new ResultFilter(ResultState: 1), 0, 0).Items);
        Assert.Empty(window.List(new ResultFilter(ResultState: 2), 0, 0).Items);
        Assert.Empty(window.List(new ResultFilter(PartId: "p"), 0, 0).Items);
    }

    [Fact]
    public void TryRemove_Takes_A_Result_Back_Out()
    {
        var window = new OpcUaResultWindow(10);
        window.TryAdd(R("a"));
        window.TryAdd(R("b"));
        Assert.True(window.TryRemove("a"));
        Assert.False(window.TryRemove("a"));
        Assert.Equal(new[] { "b" }, window.List(new ResultFilter(), 0, 0).Items.Select(r => r.ResultId));
    }
}

public sealed class MapperOpcUaResultTests
{
    private static string Body(InspectResultV2 d) => OpcUaSamples.Entry(d).Body;

    [Fact]
    public void Maps_Every_Mandatory_Field_From_The_Envelope()
    {
        var d = OpcUaSamples.Decision("d-1", "line-a", review: false);
        Assert.True(ResultMapper.TryMap(Body(d), new ScenarioFacts("abc123abc123", null), out var r, out var error), error);
        Assert.Equal("d-1", r!.ResultId);
        Assert.Equal("d-1", r.JobId);
        Assert.Equal("line-a", r.InternalRecipeId);
        Assert.Equal("abc123abc123", r.InternalConfigurationId);
        Assert.False(r.IsPartial);
        Assert.Equal(1, r.ResultState);
        Assert.Equal(new DateTime(2026, 11, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234567), r.CreationTime);
        Assert.Equal(DateTimeKind.Utc, r.CreationTime.Kind);
        Assert.Equal("OK", r.Verdict);
        Assert.Equal("vlm", r.SettledByTier);
        Assert.False(r.RequiresReview);
        Assert.Null(r.Count);
    }

    [Theory]
    [InlineData("OK", null, "OK")]
    [InlineData("ok", null, "OK")]
    [InlineData("NG", null, "NG")]
    [InlineData("scratch", "inspection", "NG")]
    [InlineData("MATCH", "verification", "OK")]
    [InlineData("MISMATCH", "verification", "NG")]
    [InlineData("OK", "verification", "NG")]   // 오투입 확인에서 좋은 쪽은 MATCH 뿐이다(파이프라인의 v1 보기와 같은 규칙)
    public void Verdict_Follows_The_Pipelines_Ok_Ng_View(string cls, string? type, string expected) =>
        Assert.Equal(expected, ResultMapper.VerdictOf(cls, type));

    [Fact]
    public void Json_Is_The_Envelope_Data_Verbatim_Not_A_Reserialization()
    {
        var d = OpcUaSamples.Decision("d-2", findings: "한글 — ünïcode");
        var body = Body(d);
        Assert.True(ResultMapper.TryMap(body, ScenarioFacts.Unknown, out var r, out _));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(doc.RootElement.GetProperty("data").GetRawText(), r!.Json);
        Assert.Contains("\\u", r.Json);   // 기본 옵션 직렬화의 이스케이프가 그대로 — 다시 쓰지 않았다
    }

    [Fact]
    public void Counting_Total_Becomes_Count()
    {
        Assert.True(ResultMapper.TryMap(Body(OpcUaSamples.Decision("d-3", count: 4)), ScenarioFacts.Unknown, out var r, out _));
        Assert.Equal(4, r!.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"data\":{\"decision_id\":\"x\"}}")]
    [InlineData("{\"data\":{\"decision_id\":\"x\",\"scenario_id\":\"s\",\"timestamp\":\"not-a-time\",\"class\":\"OK\"}}")]
    public void A_Body_It_Cannot_Read_Is_An_Error_Not_An_Exception(string body)
    {
        Assert.False(ResultMapper.TryMap(body, ScenarioFacts.Unknown, out var r, out var error));
        Assert.Null(r);
        Assert.StartsWith("malformed event", error);
    }

    [Fact]
    public void Fingerprint_Ignores_Key_Order_And_Whitespace_But_Not_Content()
    {
        var a = ScenarioFacts.FromScenarioJson("{\"b\":1,\"a\":{\"y\":[1,2],\"x\":\"v\"}}");
        var b = ScenarioFacts.FromScenarioJson("{ \"a\" : { \"x\":\"v\", \"y\":[1, 2] },\n \"b\": 1 }");
        var c = ScenarioFacts.FromScenarioJson("{\"b\":1,\"a\":{\"y\":[2,1],\"x\":\"v\"}}");
        Assert.Equal(a.ConfigurationId, b.ConfigurationId);
        Assert.NotEqual(a.ConfigurationId, c.ConfigurationId);   // 배열 순서는 의미가 있다
        Assert.Matches("^[0-9a-f]{12}$", a.ConfigurationId);
    }

    [Fact]
    public void Fingerprint_Reads_The_Scenario_Type_And_Survives_Unreadable_Json()
    {
        Assert.Equal("verification", ScenarioFacts.FromScenarioJson("{\"type\":\"verification\"}").Type);
        Assert.Null(ScenarioFacts.FromScenarioJson("{\"name\":\"x\"}").Type);
        Assert.Equal(ScenarioFacts.Unknown, ScenarioFacts.FromScenarioJson("{ broken"));
    }
}

public sealed class TransportOpcUaResultTests
{
    [Fact]
    public async Task The_Production_Composition_Wires_The_OpcUa_Destination_Into_The_Publisher_And_The_Transports()
    {
        // Program.cs 가 하는 그대로: AddUVision 다음 AddUVisionOpcUa. 꺼져 있으면 받는 곳도 발행 대상도 늘지 않는다.
        var root = Path.Combine(Path.GetTempPath(), "uvision-opcua-compose-" + Guid.NewGuid().ToString("N"));
        ServiceProvider Build(bool enabled)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UVision:Vlm:Provider"] = "mock",
                ["UVision:Storage:DataPath"] = root,
                ["UVision:Events:OpcUa:Enabled"] = enabled ? "true" : "false",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddUVision(config, root);
            services.AddUVisionOpcUa(config, root);
            return services.BuildServiceProvider();
        }

        await using (var off = Build(enabled: false))
        {
            Assert.Empty(off.GetRequiredService<EventTransports>().Destinations);
            Assert.False(off.GetRequiredService<DecisionEventPublisher>().IsEnabled);
            Assert.Null(off.GetService<OpcUaResultHub>());
        }
        await using (var on = Build(enabled: true))
        {
            var destination = Assert.Single(on.GetRequiredService<EventTransports>().Destinations);
            Assert.Equal("opcua", destination.Name);
            Assert.True(on.GetRequiredService<DecisionEventPublisher>().IsEnabled);
            Assert.NotNull(on.GetRequiredService<OpcUaResultHub>());
        }
    }

    [Fact]
    public async Task Returns_An_Error_Not_An_Exception_When_The_Server_Is_Not_Running()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvision-opcua-down-" + Guid.NewGuid().ToString("N"));
        var transport = new OpcUaTransport(
            new OpcUaResultHub(10),
            new ScenarioFactsReader(new UVision.Api.Storage.StoragePaths(new UVision.Api.Configuration.StorageOptions { DataPath = root }, root)));
        var error = await transport.SendAsync("line-a", OpcUaSamples.Entry(OpcUaSamples.Decision("down-1")), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal("OPC UA server not running", error);
    }

    [Fact]
    public void The_Destination_Source_Is_Named_opcua_And_Is_Registered_Only_When_Enabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvision-opcua-reg-" + Guid.NewGuid().ToString("N"));
        IEnumerable<IEventDestinationSource> Sources(bool enabled)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UVision:Storage:DataPath"] = root,
                ["UVision:Events:OpcUa:Enabled"] = enabled ? "true" : "false",
            });
            builder.Services.AddUVisionOpcUa(builder.Configuration, root);
            return builder.Services.BuildServiceProvider().GetServices<IEventDestinationSource>().ToArray();
        }

        Assert.Empty(Sources(enabled: false));
        var destination = Assert.Single(Assert.Single(Sources(enabled: true)).Destinations);
        Assert.Equal("opcua", destination.Name);
        Assert.Equal(OpcUaDestinationSource.Kind, destination.Kind);
        Assert.Equal("opcua", OpcUaDestinationSource.Kind);
    }
}
