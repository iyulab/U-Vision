using System.Globalization;
using System.Text.Json;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Services.Events;

/// <summary>
/// 판정 사건을 받는 곳마다 outbox 파일로 남긴다(파일 정본 — DB 없음). 보내기는 <see cref="WebhookDispatcher"/> 가
/// 따로 한다 — 판정 응답이 받는 쪽의 느림 · 장애를 기다리지 않게. 받는 곳이 없으면 아무것도 쓰지 않는다.
/// </summary>
public sealed class DecisionEventPublisher(StoragePaths paths, EventsOptions options, WebhookDeliverySignal signal)
{
    private static readonly JsonSerializerOptions Compact = new();

    public bool IsEnabled => options.Webhooks.Count > 0;

    public async Task PublishAsync(InspectResultV2 decision, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return;
        var evt = DecisionEvent.From(decision);
        var entry = new OutboxEntry { EventId = evt.Id, Body = JsonSerializer.Serialize(evt, Compact) };
        // 파일 이름이 시각 순이 되게 — 받는 곳마다 먼저 생긴 사건부터 보낸다.
        var name = DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture)
            + "_" + evt.Id + ".json";
        foreach (var hook in options.Webhooks)
            await StoragePaths.AtomicWriteJsonAsync(
                Path.Combine(paths.EventOutboxDir(decision.ScenarioId, hook.Name), name), entry, cancellationToken);
        signal.Notify();
    }
}

/// <summary>새 사건이 생겼다고 보내기 서비스를 깨운다(주기를 기다리지 않게).</summary>
public sealed class WebhookDeliverySignal
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
