using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UVision.Api.Configuration;
using UVision.Api.Storage;

namespace UVision.Api.Services.Events;

/// <summary><c>GET {ApiBasePath}/events/status</c> — 받는 곳마다 보내기를 기다리는 · 포기한 사건 수.</summary>
public sealed record EventsStatusResponse
{
    /// <summary>받는 곳이 하나라도 설정됐나.</summary>
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    [JsonPropertyName("webhooks")] public required IReadOnlyList<WebhookStatus> Webhooks { get; init; }
}

public sealed record WebhookStatus
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    /// <summary>보내기를 기다리는 사건 수(재시도 대기 포함).</summary>
    [JsonPropertyName("pending")] public required int Pending { get; init; }
    /// <summary>보내기를 포기하고 <c>dead/</c> 로 옮긴 사건 수.</summary>
    [JsonPropertyName("dead")] public required int Dead { get; init; }
    /// <summary>가장 오래 기다린 사건이 생긴 시각(UTC) — 없으면 생략.</summary>
    [JsonPropertyName("oldest_pending_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OldestPendingAt { get; init; }
    /// <summary>
    /// 가장 오래 기다린 사건의 마지막 실패 — HTTP 상태나 예외 종류만(예: <c>HTTP 503</c> · <c>HttpRequestException</c>).
    /// 받는 곳 주소가 담길 수 있는 오류 문구는 내보내지 않는다(전문은 outbox 파일과 서버 로그).
    /// </summary>
    [JsonPropertyName("last_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastError { get; init; }
}

/// <summary>outbox 디렉터리를 세어 받는 곳별 상태를 만든다(파일 정본 — 따로 저장하는 상태 없음).</summary>
public sealed class WebhookStatusReader(StoragePaths paths, EventsOptions options)
{
    public EventsStatusResponse Read()
    {
        var list = new List<WebhookStatus>();
        foreach (var hook in options.Webhooks)
        {
            var pending = new List<string>();
            var dead = 0;
            foreach (var scenarioId in paths.ScenarioIds())
            {
                string dir;
                try { dir = paths.EventOutboxDir(scenarioId, hook.Name); }
                catch (ArgumentException) { continue; }
                if (!Directory.Exists(dir))
                    continue;
                pending.AddRange(Directory.EnumerateFiles(dir, "*.json"));
                var deadDir = paths.EventDeadDir(scenarioId, hook.Name);
                if (Directory.Exists(deadDir))
                    dead += Directory.EnumerateFiles(deadDir, "*.json").Count();
            }
            var oldest = pending.OrderBy(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
            list.Add(new WebhookStatus
            {
                Name = hook.Name,
                Pending = pending.Count,
                Dead = dead,
                OldestPendingAt = oldest is null ? null : CreatedAt(oldest),
                LastError = oldest is null ? null : ErrorKind(oldest),
            });
        }
        return new EventsStatusResponse { Enabled = options.Webhooks.Count > 0, Webhooks = list };
    }

    // 파일 이름 앞부분이 사건이 생긴 시각(yyyyMMddHHmmssfffffff, UTC — DecisionEventPublisher).
    private static string? CreatedAt(string file)
    {
        var stamp = Path.GetFileName(file).Split('_', 2)[0];
        return DateTime.TryParseExact(stamp, "yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToString("o", CultureInfo.InvariantCulture)
            : null;
    }

    private static string? ErrorKind(string file)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<OutboxEntry>(File.ReadAllText(file));
            // "HTTP 503" 은 그대로, "HttpRequestException: …" 은 예외 이름만.
            return entry?.LastError?.Split(':', 2)[0];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
