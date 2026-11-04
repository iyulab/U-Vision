using System.Text.Json;
using System.Text.Json.Serialization;

namespace UVision.Api.Services.Events;

/// <summary>
/// 엣지 노드가 보낸 규칙 사건 한 건 — 판정 사건과 같은 CloudEvents 1.0 JSON 봉투(<c>type</c> 만 다르다). 받는 곳(웹훅 · MQTT)이 한
/// 봉투 모양으로 두 종류를 다 받고, <c>id</c> 로 중복을 거른다. OPC UA 는 «결과 관리» 모양이라 판정 결과만 싣는다 — 이 종류는 건너뛴다.
/// </summary>
public sealed record EdgeEventEnvelope
{
    public const string EventType = "u-vision.edge.event";

    [JsonPropertyName("specversion")] public string SpecVersion { get; init; } = "1.0";

    /// <summary>노드가 만든 UUIDv7 — 받는 쪽이 중복 수신을 거르는 키.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("source")] public required string Source { get; init; }

    [JsonPropertyName("type")] public string Type { get; init; } = EventType;

    /// <summary>사건이 일어난 때 — 노드 시계에 오프셋을 더한 보정 시각, 오프셋을 모르면 노드 시계 그대로. 원본은 <c>data</c> 에 있다.</summary>
    [JsonPropertyName("time")] public required string Time { get; init; }

    [JsonPropertyName("subject")] public required string Subject { get; init; }

    [JsonPropertyName("datacontenttype")] public string DataContentType { get; init; } = "application/json";

    [JsonPropertyName("data")] public required EdgeEventRecord Data { get; init; }
}

/// <summary>
/// 받은 사건의 기록(= <c>edge/nodes/{node}/events/{date}.jsonl</c> 의 한 줄 = 봉투의 <c>data</c>). 시각은 <b>이중 기록</b>이다:
/// <c>device_time</c>(노드 시계, 원본·불변) · <c>received_at</c>(서버) · <c>clock_offset_ms</c>(에이전트의 추정) — 보정값
/// <c>corrected_time</c> 은 파생이라 따로 둔다.
/// </summary>
public sealed record EdgeEventRecord
{
    [JsonPropertyName("node_id")] public required string NodeId { get; init; }

    [JsonPropertyName("event_id")] public required string EventId { get; init; }

    [JsonPropertyName("seq")] public required long Seq { get; init; }

    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("kind")] public required string Kind { get; init; }

    [JsonPropertyName("device_time")] public required string DeviceTime { get; init; }

    [JsonPropertyName("received_at")] public required string ReceivedAt { get; init; }

    [JsonPropertyName("clock_offset_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ClockOffsetMs { get; init; }

    /// <summary><c>device_time + clock_offset_ms</c> — 오프셋을 알 때만. 파생값이다.</summary>
    [JsonPropertyName("corrected_time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrectedTime { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }
}
