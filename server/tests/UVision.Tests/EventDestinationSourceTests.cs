using Microsoft.Extensions.Logging.Abstractions;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Events;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 받는 곳 공급자 이음새(<see cref="IEventDestinationSource"/>) — 호스트가 설정 파일 밖의 받는 곳(무거운 전송)을 더하는 길.
/// 공급자가 없으면 이음새가 없던 때와 같고, 있으면 발행 · 보내기 · 상태가 그 곳을 똑같이 다룬다.
/// </summary>
public class EventDestinationSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvision-dest-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private StoragePaths Paths => new(new StorageOptions { DataPath = _root }, AppContext.BaseDirectory);

    private static InspectResultV2 Decision(string id) => new()
    {
        DecisionId = id,
        ScenarioId = "demo",
        Timestamp = "2026-11-03T12:00:00.0000000Z",
        Class = "OK",
    };

    [Fact]
    public async Task Without_Sources_Nothing_Changes()
    {
        var options = new EventsOptions();
        var publisher = new DecisionEventPublisher(Paths, options, new EventDeliverySignal(), extra: []);
        Assert.False(publisher.IsEnabled);
        await publisher.PublishAsync(Decision("img_a"), CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(_root, "demo", "events")));
        Assert.False(new EventStatusReader(Paths, options, extra: []).Read().Enabled);
        await using var transports = new EventTransports(options, new NoHttp(), extra: []);
        Assert.Empty(transports.Destinations);
    }

    [Fact]
    public async Task Source_Destination_Gets_Outbox_Entry_Delivery_And_Counts_As_Enabled()
    {
        var recorder = new Recorder();
        var source = new FixedSource(new EventDestination("fake", "fake", recorder));
        var options = new EventsOptions();
        var publisher = new DecisionEventPublisher(Paths, options, new EventDeliverySignal(), [source]);

        Assert.True(publisher.IsEnabled); // 설정된 웹훅 · MQTT 가 없어도 공급자가 있으면 사건을 남긴다
        await publisher.PublishAsync(Decision("img_a"), CancellationToken.None);
        Assert.Single(Directory.GetFiles(Paths.EventOutboxDir("demo", "fake"), "*.json"));
        Assert.True(new EventStatusReader(Paths, options, [source]).Read().Enabled);

        await using var transports = new EventTransports(options, new NoHttp(), [source]);
        var dispatcher = new EventDispatcher(Paths, options, transports, NullLogger<EventDispatcher>.Instance);
        Assert.Equal(1, await dispatcher.DeliverDueAsync(_now, CancellationToken.None));
        Assert.Equal(["img_a"], recorder.EventIds);
        Assert.Empty(Directory.GetFiles(Paths.EventOutboxDir("demo", "fake"), "*.json"));
    }

    [Fact]
    public async Task Source_Failure_Uses_The_Same_Retry_And_Order_As_Other_Transports()
    {
        var recorder = new Recorder { Fail = "boom: details" };
        var source = new FixedSource(new EventDestination("fake", "fake", recorder));
        var options = new EventsOptions { MaxAttempts = 2 };
        var publisher = new DecisionEventPublisher(Paths, options, new EventDeliverySignal(), [source]);
        await publisher.PublishAsync(Decision("img_a"), CancellationToken.None);
        await publisher.PublishAsync(Decision("img_b"), CancellationToken.None);
        await using var transports = new EventTransports(options, new NoHttp(), [source]);
        var dispatcher = new EventDispatcher(Paths, options, transports, NullLogger<EventDispatcher>.Instance);

        Assert.Equal(0, await dispatcher.DeliverDueAsync(_now, CancellationToken.None));
        Assert.Equal(["img_a"], recorder.EventIds); // 첫 사건만 시도하고 뒤 사건은 기다린다
        await dispatcher.DeliverDueAsync(_now.AddSeconds(11), CancellationToken.None);
        Assert.Single(Directory.GetFiles(Paths.EventDeadDir("demo", "fake"), "*.json"));
    }

    [Fact]
    public void Source_Name_Clashing_With_A_Configured_Destination_Is_Rejected()
    {
        var options = new EventsOptions
        {
            Webhooks = [new WebhookOptions { Name = "mes", Url = "http://receiver.test/hook" }],
        };
        var clash = new FixedSource(new EventDestination("mes", "fake", new Recorder()));
        var ex = Assert.Throws<InvalidOperationException>(() => new EventTransports(options, new NoHttp(), [clash]));
        Assert.Contains("'mes'", ex.Message);
    }

    [Fact]
    public async Task Disposing_Transports_Leaves_Source_Owned_Transports_Alone()
    {
        var recorder = new Recorder();
        var source = new FixedSource(new EventDestination("fake", "fake", recorder));
        var transports = new EventTransports(new EventsOptions(), new NoHttp(), [source]);
        await transports.DisposeAsync();
        Assert.False(recorder.Disposed); // 수명은 공급자(DI 싱글턴)의 몫
    }

    private sealed class FixedSource(params EventDestination[] destinations) : IEventDestinationSource
    {
        public IReadOnlyList<EventDestination> Destinations { get; } = destinations;
    }

    private sealed class Recorder : IEventTransport, IAsyncDisposable
    {
        public List<string> EventIds { get; } = [];
        public string? Fail { get; set; }
        public bool Disposed { get; private set; }

        public Task<string?> SendAsync(string scenarioId, OutboxEntry entry, DateTimeOffset now, CancellationToken ct)
        {
            EventIds.Add(entry.EventId);
            return Task.FromResult(Fail);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no HTTP expected");
    }
}
