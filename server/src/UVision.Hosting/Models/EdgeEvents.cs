using System.Text.Json;
using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>엣지 에이전트가 보내는 사건 한 건 — 노드의 규칙이 낸 것(수량 변화 · 경보 …). 판정이 필요한 크롭은 이 길이 아니라 <c>/v2/inspect</c> 로 간다.</summary>
public sealed record EdgeEventInput
{
    /// <summary>노드가 만든 UUIDv7 — 재시작에도 유일하고 시간 순. 중복 제거 키는 <c>(node_id, event_id)</c> 다.</summary>
    [JsonPropertyName("event_id")] public string? EventId { get; init; }

    /// <summary>노드 안 단조 증가 번호(0 이상) — 서버는 이것으로 <b>구멍만</b> 감지한다(도착 순서에 의존하지 않는다).</summary>
    [JsonPropertyName("seq")] public long? Seq { get; init; }

    /// <summary>사건이 속한 시나리오 — 이 노드에 배정된 것이어야 한다.</summary>
    [JsonPropertyName("scenario_id")] public string? ScenarioId { get; init; }

    /// <summary>규칙의 종류(<c>count_changed</c> · <c>alarm</c> …, <c>[a-z0-9_.-]</c> 64자 이하).</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }

    /// <summary>노드 시계의 사건 시각 — 시간대가 있는 ISO 8601.</summary>
    [JsonPropertyName("device_time")] public string? DeviceTime { get; init; }

    /// <summary>규칙이 낸 내용(자유 JSON 객체, 직렬화 16 KiB 이하).</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }
}

/// <summary>사건 배치 요청.</summary>
public sealed record EdgeEventsRequest
{
    /// <summary>한 번에 최대 500건.</summary>
    [JsonPropertyName("events")] public required IReadOnlyList<EdgeEventInput> Events { get; init; }

    /// <summary>에이전트가 추정한 «서버 시각 − 노드 시각»(밀리초) — 폴링 응답의 <c>Date</c> 헤더로 잰다. 원본 시각은 바꾸지 않고 따로 쌓는다.</summary>
    [JsonPropertyName("clock_offset_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ClockOffsetMs { get; init; }
}

/// <summary>사건 한 건의 처리 결과 — <c>accepted</c> | <c>duplicate</c> | <c>rejected</c>(+ <c>code</c>).</summary>
public sealed record EdgeEventResult
{
    [JsonPropertyName("event_id")] public required string EventId { get; init; }

    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>거절 사유 코드 — <c>invalid_event_id</c> · <c>invalid_seq</c> · <c>invalid_kind</c> · <c>invalid_time</c> · <c>invalid_data</c> ·
    /// <c>invalid_scenario</c> · <c>scenario_not_assigned</c> · <c>scenario_unknown</c>.</summary>
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }
}

/// <summary>받은 <c>seq</c> 의 빈 구간(양 끝 포함) — 아직 오지 않은 번호들. 늦게 오면 메워진다.</summary>
public sealed record SeqRange
{
    [JsonPropertyName("from")] public required long From { get; init; }

    [JsonPropertyName("to")] public required long To { get; init; }
}

/// <summary>사건 배치 응답 — 항목별 결과(요청 순)와 지금 남은 <c>seq</c> 구멍.</summary>
public sealed record EdgeEventsResponse
{
    [JsonPropertyName("results")] public required IReadOnlyList<EdgeEventResult> Results { get; init; }

    /// <summary>받은 가장 큰 <c>seq</c> 아래의 빈 구간(최대 20개) — 비었으면 생략.</summary>
    [JsonPropertyName("missing_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SeqRange>? MissingSeq { get; init; }
}

/// <summary>노드에 시나리오를 배정하는 요청(관리자 PIN) — 노드가 사건을 쓸 수 있는 범위이자 구성을 받을 범위다.</summary>
public sealed record EdgeScenariosInput
{
    [JsonPropertyName("scenarios")] public required IReadOnlyList<string> Scenarios { get; init; }
}
