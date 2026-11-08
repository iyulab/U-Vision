using System.Text.Json.Serialization;
using UVision.Api.Services.Events;

namespace UVision.Api.Models;

/// <summary>
/// 노드가 보낸 사건을 관리자가 읽는 모양(읽기 전용 · 판정에 쓰이지 않는다) — 최근 사건 목록과, 그 사건들에서 파생한 «지금 상태».
/// 파생값은 장부에 이미 있는 사건을 다시 읽어 계산한 것이라 새 진실이 아니다 — 사건이 모두 도착하지 않았으면 그만큼 낡았다.
/// </summary>
public sealed record EdgeNodeEventsView
{
    [JsonPropertyName("node_id")] public required string NodeId { get; init; }

    /// <summary>최근 사건 — 최신이 먼저. 장부의 원본 기록 그대로다(시각은 이중 기록).</summary>
    [JsonPropertyName("events")] public required IReadOnlyList<EdgeEventRecord> Events { get; init; }

    /// <summary>사건에서 파생한 규칙별 · 소스별 마지막 상태.</summary>
    [JsonPropertyName("current")] public required EdgeCurrentState Current { get; init; }

    /// <summary>이 응답을 만들려고 읽은 사건 수(파생값의 범위).</summary>
    [JsonPropertyName("scanned")] public required int Scanned { get; init; }

    /// <summary>읽기 한도에 걸려 더 오래된 사건을 읽지 않았으면 true — 그 경우 오래 전에 한 번만 난 규칙의 상태는 빠질 수 있다.</summary>
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
}

/// <summary>사건에서 파생한 «지금 상태» — 구역 안 개수 · 경보 · 소스(카메라) 상태.</summary>
public sealed record EdgeCurrentState
{
    [JsonPropertyName("counts")] public required IReadOnlyList<EdgeCurrentCount> Counts { get; init; }

    [JsonPropertyName("alarms")] public required IReadOnlyList<EdgeCurrentAlarm> Alarms { get; init; }

    [JsonPropertyName("sources")] public required IReadOnlyList<EdgeCurrentSource> Sources { get; init; }
}

/// <summary>구역 안 개수 규칙의 마지막으로 안정된 개수. 이 값이 «지금도 맞는가» 는 같은 소스의 <see cref="EdgeCurrentSource"/> 상태와 함께 읽는다.</summary>
public sealed record EdgeCurrentCount
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("rule")] public required string Rule { get; init; }

    [JsonPropertyName("source")] public string? Source { get; init; }

    [JsonPropertyName("zone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Zone { get; init; }

    [JsonPropertyName("class")] public string? Class { get; init; }

    [JsonPropertyName("count")] public required int Count { get; init; }

    /// <summary>그 개수가 시작된 시각(노드 시계).</summary>
    [JsonPropertyName("since")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Since { get; init; }

    /// <summary>이 사건의 노드 시계 시각.</summary>
    [JsonPropertyName("device_time")] public required string DeviceTime { get; init; }

    [JsonPropertyName("received_at")] public required string ReceivedAt { get; init; }

    /// <summary>노드의 구성이 낡은 채로 낸 사건이면 true(생략 = 아님).</summary>
    [JsonPropertyName("config_stale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ConfigStale { get; init; }
}

/// <summary>경보 규칙의 마지막 상태 — <c>raised</c>(올라가 있음) | <c>cleared</c>(내려감).</summary>
public sealed record EdgeCurrentAlarm
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("rule")] public required string Rule { get; init; }

    [JsonPropertyName("source")] public string? Source { get; init; }

    [JsonPropertyName("zone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Zone { get; init; }

    [JsonPropertyName("class")] public string? Class { get; init; }

    [JsonPropertyName("state")] public required string State { get; init; }

    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; init; }

    [JsonPropertyName("since")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Since { get; init; }

    [JsonPropertyName("device_time")] public required string DeviceTime { get; init; }

    [JsonPropertyName("received_at")] public required string ReceivedAt { get; init; }

    [JsonPropertyName("config_stale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ConfigStale { get; init; }
}

/// <summary>소스(카메라)의 마지막으로 알려진 상태 — <c>ok</c> | <c>no_signal</c> | <c>degraded</c>. 사건이 한 번도 없었던 소스는 목록에 없다(= 처음부터 <c>ok</c> 로 본다).</summary>
public sealed record EdgeCurrentSource
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("source")] public string? Source { get; init; }

    [JsonPropertyName("camera")] public string? Camera { get; init; }

    [JsonPropertyName("state")] public required string State { get; init; }

    [JsonPropertyName("device_time")] public required string DeviceTime { get; init; }

    [JsonPropertyName("received_at")] public required string ReceivedAt { get; init; }
}
