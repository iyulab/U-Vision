using System.Globalization;
using System.Text.Json;
using UVision.Api.Configuration;
using UVision.Api.Storage;

namespace UVision.Api.Services.Events;

/// <summary>
/// outbox 의 사건을 받는 곳(웹훅 · MQTT)으로 보낸다 — 최소 한 번(at-least-once). 전송이 성공하면 지우고, 실패하면
/// 횟수를 늘려 다음 시도 시각을 지수로 미루며(최대 1시간), <see cref="EventsOptions.MaxAttempts"/> 에 이르면 <c>dead/</c> 로 옮긴다.
/// 받는 곳 하나가 실패하거나 쉬는 중이면 그 틱에는 그곳의 뒤 사건을 보내지 않는다(순서 유지 · 내려간 곳을 두드리지 않게).
/// 재시도 · 순서 · 포기는 전송에 상관없이 같다 — 전송은 한 건을 보내는 일만 안다(<see cref="IEventTransport"/>).
/// </summary>
public sealed class EventDispatcher(
    StoragePaths paths, EventsOptions options, EventTransports transports, ILogger<EventDispatcher> logger)
{
    private const double MaxBackoffSeconds = 3600;

    /// <summary>지금 보낼 차례인 사건을 보낸다. 보낸(지운) 수를 돌려준다.</summary>
    public async Task<int> DeliverDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var delivered = 0;
        foreach (var scenarioId in paths.ScenarioIds())
        foreach (var destination in transports.Destinations)
        {
            string dir;
            try { dir = paths.EventOutboxDir(scenarioId, destination.Name); }
            catch (ArgumentException) { continue; } // 시나리오가 아닌 디렉터리
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
            {
                var entry = await ReadAsync(file, cancellationToken);
                if (entry is null)
                    continue;
                if (entry.NextAttemptAt is { } next
                    && DateTimeOffset.Parse(next, CultureInfo.InvariantCulture) > now)
                    break;
                var error = await destination.Transport.SendAsync(scenarioId, entry, now, cancellationToken);
                if (error is null)
                {
                    File.Delete(file);
                    delivered++;
                    continue;
                }
                await FailAsync(scenarioId, destination, file, entry, error, now, cancellationToken);
                break;
            }
        }
        return delivered;
    }

    private async Task FailAsync(
        string scenarioId, EventDestination destination, string file, OutboxEntry entry, string error,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var attempts = entry.Attempts + 1;
        if (attempts >= options.MaxAttempts)
        {
            var dead = paths.EventDeadDir(scenarioId, destination.Name);
            Directory.CreateDirectory(dead);
            await StoragePaths.AtomicWriteJsonAsync(
                file, entry with { Attempts = attempts, LastError = error }, cancellationToken);
            File.Move(file, Path.Combine(dead, Path.GetFileName(file)), overwrite: true);
            logger.LogWarning("{Kind} {Name} 사건 {EventId} 보내기 포기({Attempts}회) — {Error}",
                destination.Kind, destination.Name, entry.EventId, attempts, error);
            return;
        }
        var backoff = TimeSpan.FromSeconds(Math.Min(10 * Math.Pow(2, attempts - 1), MaxBackoffSeconds));
        await StoragePaths.AtomicWriteJsonAsync(file, entry with
        {
            Attempts = attempts,
            NextAttemptAt = (now + backoff).ToString("o", CultureInfo.InvariantCulture),
            LastError = error,
        }, cancellationToken);
        logger.LogInformation("{Kind} {Name} 사건 {EventId} 실패({Attempts}회, {Backoff} 뒤 재시도) — {Error}",
            destination.Kind, destination.Name, entry.EventId, attempts, backoff, error);
    }

    private async Task<OutboxEntry?> ReadAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<OutboxEntry>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogWarning(ex, "outbox 파일을 읽지 못함 — 건너뜀 ({File})", file);
            return null;
        }
    }
}

/// <summary>보내기 주기 — 받는 곳이 없으면 바로 끝난다(부수효과 0). 새 사건이 생기면 주기를 기다리지 않는다.</summary>
public sealed class EventDeliveryService(
    EventDispatcher dispatcher, EventDeliverySignal signal, EventTransports transports, EventsOptions options,
    ILogger<EventDeliveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (transports.Destinations.Count == 0)
            return;
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.DeliveryIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await dispatcher.DeliverDueAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "사건 보내기 주기 실패 — 다음 주기 재시도"); }

            try { await signal.WaitAsync(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
