using System.Globalization;
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using UVision.Api.Configuration;

namespace UVision.Api.Services.Events;

/// <summary>
/// 사건을 받는 곳 하나로 실제로 보내는 일 — outbox · 재시도 · 순서는 <see cref="EventDispatcher"/> 가 모든 전송에
/// 똑같이 하고, 전송이 아는 것은 «한 건을 보내고 성공이면 null, 실패면 오류 문구» 뿐이다.
/// 오류 문구의 앞부분(첫 <c>:</c> 앞)만 상태 보기에 나가므로, 받는 곳 주소가 담길 수 있는 문구는 그 뒤에 둔다.
/// </summary>
public interface IEventTransport
{
    Task<string?> SendAsync(string scenarioId, OutboxEntry entry, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>받는 곳 하나 — 이름이 outbox 디렉터리다(웹훅 · MQTT 를 통틀어 겹치지 않는다).</summary>
public sealed record EventDestination(string Name, string Kind, IEventTransport Transport)
{
    public const string WebhookKind = "webhook";
    public const string MqttKind = "mqtt";
}

/// <summary>설정된 받는 곳마다 전송을 만들어 쥔다. 받는 곳이 없으면 비어 있다(부수효과 0).</summary>
public sealed class EventTransports : IAsyncDisposable
{
    public EventTransports(EventsOptions options, IHttpClientFactory httpClients)
    {
        var all = new List<EventDestination>();
        foreach (var hook in options.Webhooks)
            all.Add(new EventDestination(hook.Name, EventDestination.WebhookKind, new WebhookTransport(hook, httpClients)));
        foreach (var mqtt in options.Mqtt)
            all.Add(new EventDestination(mqtt.Name, EventDestination.MqttKind, new MqttTransport(mqtt)));
        Destinations = all;
    }

    public IReadOnlyList<EventDestination> Destinations { get; }

    public async ValueTask DisposeAsync()
    {
        foreach (var destination in Destinations)
            if (destination.Transport is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
    }
}

/// <summary>웹훅 — JSON 본문을 POST 하고 Standard Webhooks 서명 헤더를 붙인다(비밀이 있으면).</summary>
public sealed class WebhookTransport(WebhookOptions hook, IHttpClientFactory httpClients) : IEventTransport
{
    public const string HttpClientName = "u-vision-webhooks";

    public async Task<string?> SendAsync(
        string scenarioId, OutboxEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url)
        {
            Content = new StringContent(entry.Body, Encoding.UTF8, "application/json"),
        };
        var timestamp = now.ToUnixTimeSeconds();
        request.Headers.Add(WebhookSignature.IdHeader, entry.EventId);
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(hook.Secret))
            request.Headers.Add(WebhookSignature.SignatureHeader,
                WebhookSignature.Sign(WebhookSignature.SecretBytes(hook.Secret), entry.EventId, timestamp, entry.Body));
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}

/// <summary>
/// MQTT 5 — 같은 CloudEvents 봉투를 <b>구조적 모드</b>(본문 = 봉투 JSON, <c>content-type: application/cloudevents+json</c>)로
/// <c>{TopicPrefix}/{scenarioId}</c> 에 낸다. 서비스 품질 1(최소 한 번 — 브로커의 PUBACK 을 받아야 성공)이 기본이라 outbox 의
/// 최소 한 번과 맞고, 같은 사건이 다시 와도 봉투 <c>id</c> 로 거른다(웹훅과 같은 규칙). 연결은 하나를 쥐고 끊기면 다음 보내기에서 다시 잇는다.
/// </summary>
public sealed class MqttTransport(MqttOptions options) : IEventTransport, IAsyncDisposable
{
    public const string ContentType = "application/cloudevents+json";
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly MqttClientFactory _factory = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IMqttClient? _client;

    /// <summary>사건 한 건이 나가는 주제 — 시나리오 id 는 화이트리스트라 와일드카드가 들어가지 않는다.</summary>
    public static string Topic(MqttOptions options, string scenarioId) =>
        options.TopicPrefix.TrimEnd('/') + "/" + scenarioId;

    public async Task<string?> SendAsync(
        string scenarioId, OutboxEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SendTimeout);
            var client = await ConnectedAsync(timeout.Token);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(Topic(options, scenarioId))
                .WithPayload(Encoding.UTF8.GetBytes(entry.Body))
                .WithContentType(ContentType)
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)options.QualityOfService)
                .Build();
            var result = await client.PublishAsync(message, timeout.Token);
            return result.IsSuccess ? null : "MQTT " + result.ReasonCode;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 끊긴 연결로 다시 시도하지 않게 버린다 — 다음 보내기가 새로 잇는다.
            await DropAsync();
            return ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IMqttClient> ConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true } alive)
            return alive;
        await DropAsync();
        var client = _factory.CreateMqttClient();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port > 0 ? options.Port : options.UseTls ? 8883 : 1883)
            .WithClientId(string.IsNullOrEmpty(options.ClientId) ? "u-vision-" + options.Name : options.ClientId)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithCleanStart();
        if (!string.IsNullOrEmpty(options.Username))
            builder = builder.WithCredentials(options.Username, options.Password);
        if (options.UseTls)
            builder = builder.WithTlsOptions(o => o.UseTls());
        var connected = await client.ConnectAsync(builder.Build(), cancellationToken);
        if (connected.ResultCode != MqttClientConnectResultCode.Success)
        {
            client.Dispose();
            throw new InvalidOperationException("MQTT connect " + connected.ResultCode);
        }
        _client = client;
        return client;
    }

    private async Task DropAsync()
    {
        var client = _client;
        _client = null;
        if (client is null)
            return;
        try { await client.DisconnectAsync(); }
        catch (Exception) { /* 이미 끊김 */ }
        client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await DropAsync(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
