using System.Globalization;
using System.Text.Json;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Services.Events;

/// <summary>
/// 판정 사건을 받는 곳(웹훅 · MQTT)마다 outbox 파일로 남긴다(파일 정본 — DB 없음). 보내기는 <see cref="EventDispatcher"/> 가
/// 따로 한다 — 판정 응답이 받는 쪽의 느림 · 장애를 기다리지 않게. 받는 곳이 없으면 아무것도 쓰지 않는다.
/// </summary>
public sealed class DecisionEventPublisher(
    StoragePaths paths, EventsOptions options, EventDeliverySignal signal,
    IEnumerable<IEventDestinationSource>? extra = null)
{
    private static readonly JsonSerializerOptions Compact = new();

    /// <summary>사건을 남길 받는 곳 이름 — 설정된 웹훅 · MQTT 와 공급자가 더한 곳(<see cref="IEventDestinationSource"/>).</summary>
    private IEnumerable<string> DestinationNames() =>
        options.Webhooks.Select(h => h.Name)
            .Concat(options.Mqtt.Select(m => m.Name))
            .Concat(extra?.SelectMany(s => s.Destinations).Select(d => d.Name) ?? []);

    public bool IsEnabled => DestinationNames().Any();

    public async Task PublishAsync(InspectResultV2 decision, CancellationToken cancellationToken)
    {
        var evt = DecisionEvent.From(decision);
        await PublishBodyAsync(decision.ScenarioId, evt.Id, JsonSerializer.Serialize(evt, Compact), cancellationToken);
    }

    /// <summary>
    /// 이미 만든 봉투 본문(CloudEvents JSON)을 시나리오의 받는 곳 outbox 마다 남긴다 — 판정 사건이 아닌 사건(엣지 노드의 규칙 사건)이
    /// 같은 outbox · 같은 재시도 · 같은 순서 보장을 쓰게 한다. 받는 곳이 없으면 아무것도 쓰지 않는다.
    /// </summary>
    public async Task PublishBodyAsync(string scenarioId, string eventId, string body, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return;
        var entry = new OutboxEntry { EventId = eventId, Body = body };
        // 파일 이름이 시각 순이 되게 — 받는 곳마다 먼저 생긴 사건부터 보낸다.
        var name = DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture)
            + "_" + eventId + ".json";
        foreach (var destination in DestinationNames())
            await StoragePaths.AtomicWriteJsonAsync(
                Path.Combine(paths.EventOutboxDir(scenarioId, destination), name), entry, cancellationToken);
        signal.Notify();
    }
}

/// <summary>새 사건이 생겼다고 보내기 서비스를 깨운다(주기를 기다리지 않게).</summary>
public sealed class EventDeliverySignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { } // 이미 깨워 둠
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);
}
