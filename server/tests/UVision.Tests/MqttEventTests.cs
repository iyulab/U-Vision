using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Events;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 판정 사건의 MQTT 전송 — 같은 outbox · 재시도 · 순서 위에서, 실제 MQTT 5 프로토콜로 프로세스 안 브로커
/// (MQTTnet.Server)에 낸다. 이 시험은 «브로커가 받은 PUBLISH 의 주제 · 서비스 품질 · content-type · 본문» 을 본다 —
/// 구독자 쪽 왕복은 별개의 브로커(Mosquitto)로 따로 확인했다.
/// </summary>
public class MqttEventTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvision-mqtt-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private StoragePaths Paths => new(new StorageOptions { DataPath = _root }, AppContext.BaseDirectory);

    private static EventsOptions Options(int port, int maxAttempts = 15, int qos = 1) => new()
    {
        MaxAttempts = maxAttempts,
        Mqtt = [new MqttOptions { Name = "plant", Host = "127.0.0.1", Port = port, QualityOfService = qos }],
    };

    private static InspectResultV2 Decision(string id, string scenario = "demo") => new()
    {
        DecisionId = id,
        ScenarioId = scenario,
        Timestamp = "2026-11-03T12:00:00.0000000Z",
        Class = "OK",
    };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record Received(string Topic, string ContentType, MqttQualityOfServiceLevel Qos, string Body);

    private static async Task<(MqttServer Server, List<Received> Messages)> StartBrokerAsync(int port)
    {
        var factory = new MqttServerFactory();
        var server = factory.CreateMqttServer(
            factory.CreateServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build());
        var messages = new List<Received>();
        server.InterceptingPublishAsync += e =>
        {
            var m = e.ApplicationMessage;
            lock (messages)
                messages.Add(new Received(m.Topic, m.ContentType ?? "", m.QualityOfServiceLevel,
                    Encoding.UTF8.GetString(m.Payload)));
            return Task.CompletedTask;
        };
        await server.StartAsync();
        return (server, messages);
    }

    private EventDispatcher Dispatcher(EventsOptions options, EventTransports transports) =>
        new(Paths, options, transports, NullLogger<EventDispatcher>.Instance);

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("MQTT 시험은 HTTP 를 쓰지 않는다");
    }

    [Fact]
    public async Task Event_Reaches_The_Broker_On_The_Scenario_Topic_As_Structured_CloudEvent_At_Least_Once()
    {
        var port = FreePort();
        var (server, messages) = await StartBrokerAsync(port);
        try
        {
            var options = Options(port);
            await using var transports = new EventTransports(options, new NoHttp());
            await new DecisionEventPublisher(Paths, options, new EventDeliverySignal())
                .PublishAsync(Decision("img_a"), CancellationToken.None);

            var sent = await Dispatcher(options, transports).DeliverDueAsync(_now, CancellationToken.None);

            Assert.Equal(1, sent);
            Assert.Empty(Directory.GetFiles(Paths.EventOutboxDir("demo", "plant"), "*.json")); // 브로커가 받으면 지운다
            var message = Assert.Single(messages);
            Assert.Equal("u-vision/events/demo", message.Topic);
            Assert.Equal("application/cloudevents+json", message.ContentType);
            Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, message.Qos);
            using var evt = JsonDocument.Parse(message.Body);
            Assert.Equal("1.0", evt.RootElement.GetProperty("specversion").GetString());
            Assert.Equal("img_a", evt.RootElement.GetProperty("id").GetString());
            Assert.Equal("u-vision.inspection.decided", evt.RootElement.GetProperty("type").GetString());
            Assert.Equal("img_a", evt.RootElement.GetProperty("data").GetProperty("decision_id").GetString());
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Exactly_Once_Option_Is_Sent_At_Qos2_And_Each_Scenario_Gets_Its_Own_Topic()
    {
        var port = FreePort();
        var (server, messages) = await StartBrokerAsync(port);
        try
        {
            var options = Options(port, qos: 2);
            options.Mqtt[0].TopicPrefix = "plant-7/vision/";
            await using var transports = new EventTransports(options, new NoHttp());
            var publisher = new DecisionEventPublisher(Paths, options, new EventDeliverySignal());
            await publisher.PublishAsync(Decision("img_a", "line-a"), CancellationToken.None);
            await publisher.PublishAsync(Decision("img_b", "line-b"), CancellationToken.None);

            Assert.Equal(2, await Dispatcher(options, transports).DeliverDueAsync(_now, CancellationToken.None));

            Assert.Equal(["plant-7/vision/line-a", "plant-7/vision/line-b"], messages.Select(m => m.Topic).Order().ToArray());
            Assert.All(messages, m => Assert.Equal(MqttQualityOfServiceLevel.ExactlyOnce, m.Qos));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Unreachable_Broker_Keeps_The_Event_Then_It_Is_Delivered_Once_The_Broker_Is_Up()
    {
        var port = FreePort();
        var options = Options(port);
        await using var transports = new EventTransports(options, new NoHttp());
        var publisher = new DecisionEventPublisher(Paths, options, new EventDeliverySignal());
        await publisher.PublishAsync(Decision("img_a"), CancellationToken.None);
        await publisher.PublishAsync(Decision("img_b"), CancellationToken.None);
        var dispatcher = Dispatcher(options, transports);

        // 브로커가 없다 — 첫 사건만 시도하고 뒤 사건은 기다린다, 10 초 뒤로 미룬다(웹훅과 같은 규칙).
        Assert.Equal(0, await dispatcher.DeliverDueAsync(_now, CancellationToken.None));
        Assert.Equal(2, Directory.GetFiles(Paths.EventOutboxDir("demo", "plant"), "*.json").Length);
        var status = new EventStatusReader(Paths, options).Read();
        var mqtt = Assert.Single(status.Mqtt!);
        Assert.Equal(2, mqtt.Pending);
        Assert.NotNull(mqtt.LastError);
        Assert.DoesNotContain("127.0.0.1", mqtt.LastError); // 주소가 담길 수 있는 문구는 내보내지 않는다

        var (server, messages) = await StartBrokerAsync(port);
        try
        {
            Assert.Equal(0, await dispatcher.DeliverDueAsync(_now.AddSeconds(5), CancellationToken.None)); // 쉬는 중
            Assert.Equal(2, await dispatcher.DeliverDueAsync(_now.AddSeconds(11), CancellationToken.None));
            Assert.Equal(["img_a", "img_b"], messages.Select(m => JsonDocument.Parse(m.Body).RootElement.GetProperty("id").GetString()).ToArray()); // 순서 유지
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Broker_Restart_Is_Survived_The_Connection_Is_Made_Again()
    {
        var port = FreePort();
        var options = Options(port);
        await using var transports = new EventTransports(options, new NoHttp());
        var publisher = new DecisionEventPublisher(Paths, options, new EventDeliverySignal());
        var dispatcher = Dispatcher(options, transports);

        var (first, firstMessages) = await StartBrokerAsync(port);
        await publisher.PublishAsync(Decision("img_a"), CancellationToken.None);
        Assert.Equal(1, await dispatcher.DeliverDueAsync(_now, CancellationToken.None));
        Assert.Single(firstMessages);
        await first.StopAsync();

        await publisher.PublishAsync(Decision("img_b"), CancellationToken.None);
        // 끊긴 연결로 한 번 실패할 수 있다 — 실패하면 outbox 에 남고 다음 틱에 새로 이어 보낸다.
        var (second, secondMessages) = await StartBrokerAsync(port);
        try
        {
            var delivered = 0;
            for (var tick = 1; tick <= 3 && delivered == 0; tick++)
                delivered = await dispatcher.DeliverDueAsync(_now.AddMinutes(tick), CancellationToken.None);
            Assert.Equal(1, delivered);
            Assert.Equal("img_b", JsonDocument.Parse(Assert.Single(secondMessages).Body).RootElement.GetProperty("id").GetString());
        }
        finally
        {
            await second.StopAsync();
        }
    }

    [Fact]
    public async Task Status_Omits_The_Mqtt_List_Unless_A_Broker_Is_Configured()
    {
        Assert.Null(new EventStatusReader(Paths, new EventsOptions()).Read().Mqtt);
        var withHook = new EventsOptions { Webhooks = [new WebhookOptions { Name = "mes", Url = "http://receiver.test/hook" }] };
        Assert.Null(new EventStatusReader(Paths, withHook).Read().Mqtt);
        Assert.NotNull(new EventStatusReader(Paths, Options(1883)).Read().Mqtt);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("Host", "")]
    [InlineData("QualityOfService", "0")]
    [InlineData("QualityOfService", "3")]
    [InlineData("Port", "70000")]
    [InlineData("TopicPrefix", "plant/+/x")]
    [InlineData("TopicPrefix", "plant/#")]
    [InlineData("TopicPrefix", "$SYS/x")]
    [InlineData("TopicPrefix", "a//b")]
    public void A_Misconfigured_Broker_Stops_The_Host_At_Start(string key, string value)
    {
        using var factory = new UVisionApiFactory();
        using var bad = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("UVision:Events:Mqtt:0:Name", "plant");
            b.UseSetting("UVision:Events:Mqtt:0:Host", "127.0.0.1");
            b.UseSetting($"UVision:Events:Mqtt:0:{key}", value);
        });
        Assert.Throws<InvalidOperationException>(() => bad.CreateClient());
    }

    [Fact]
    public void A_Webhook_And_A_Broker_May_Not_Share_A_Name()
    {
        using var factory = new UVisionApiFactory();
        using var bad = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("UVision:Events:Webhooks:0:Name", "plant");
            b.UseSetting("UVision:Events:Webhooks:0:Url", "http://127.0.0.1:9/hook");
            b.UseSetting("UVision:Events:Mqtt:0:Name", "plant");
            b.UseSetting("UVision:Events:Mqtt:0:Host", "127.0.0.1");
        });
        Assert.Throws<InvalidOperationException>(() => bad.CreateClient());
    }
}
