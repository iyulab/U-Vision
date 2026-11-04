using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>엣지 노드 등록 토큰 발급 요청(관리자 PIN).</summary>
public sealed record EdgeTokenRequest
{
    /// <summary>이 토큰으로 등록할 노드의 표시 이름(선택) — 등록 때 노드가 자기 이름을 주지 않으면 이것을 쓴다.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>유효 시간(분, 1~1440). 생략하면 60.</summary>
    [JsonPropertyName("ttl_minutes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TtlMinutes { get; init; }
}

/// <summary>발급된 일회용 등록 토큰 — <b>이 응답에서만</b> 평문을 볼 수 있다(엔진은 해시만 둔다).</summary>
public sealed record EdgeTokenResponse
{
    [JsonPropertyName("token")] public required string Token { get; init; }

    /// <summary>만료 시각(UTC ISO 8601).</summary>
    [JsonPropertyName("expires_at")] public required string ExpiresAt { get; init; }
}

/// <summary>엣지 에이전트의 등록 요청 — 일회용 토큰으로 노드가 된다.</summary>
public sealed record EdgeRegisterRequest
{
    [JsonPropertyName("token")] public required string Token { get; init; }

    /// <summary>노드 표시 이름(선택, 80자 이하). 없으면 토큰 발급 때의 이름, 그것도 없으면 노드 id.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

/// <summary>등록 결과 — 노드 자격(<c>secret</c>)은 <b>이 응답에서만</b> 평문이다. 에이전트가 디스크에 보관한다.</summary>
public sealed record EdgeRegisterResponse
{
    [JsonPropertyName("node_id")] public required string NodeId { get; init; }

    /// <summary>노드별 비밀 — 이후 요청은 <c>Authorization: Bearer {node_id}.{secret}</c>.</summary>
    [JsonPropertyName("secret")] public required string Secret { get; init; }
}

/// <summary>엣지 노드 한 대 — 관리자가 보는 목록의 행. 비밀(해시 포함)은 어떤 응답에도 없다.</summary>
public sealed record EdgeNode
{
    [JsonPropertyName("node_id")] public required string NodeId { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("registered_at")] public required string RegisteredAt { get; init; }

    /// <summary>마지막으로 인증된 요청의 시각(최대 1분 간격으로 갱신) — 아직 없으면 생략.</summary>
    [JsonPropertyName("last_seen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastSeen { get; init; }

    /// <summary>이 노드에 배정된 시나리오 — 사건을 쓸 수 있고 구성을 받을 범위. 배정 전에는 빈 목록.</summary>
    [JsonPropertyName("scenarios")] public IReadOnlyList<string> Scenarios { get; init; } = [];

    /// <summary>마지막으로 받은 «서버 시각 − 노드 시각»(밀리초) — 에이전트가 사건 배치에 실어 보낸다.</summary>
    [JsonPropertyName("clock_offset_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ClockOffsetMs { get; init; }

    /// <summary>오프셋의 크기가 임계(60초)를 넘으면 true — «시계 어긋남» 표시일 뿐 경보도 거절도 아니다.</summary>
    [JsonPropertyName("clock_skewed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ClockSkewed { get; init; }

    /// <summary>에이전트가 마지막으로 보고한 자기 상태(outbox 깊이 · 버린 크롭) — 아직 한 번도 보고하지 않았으면 생략.</summary>
    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EdgeNodeStatus? Status { get; init; }

    /// <summary>회수된 시각 — 있으면 이 노드의 자격은 더는 통하지 않는다.</summary>
    [JsonPropertyName("revoked_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RevokedAt { get; init; }
}

/// <summary>
/// 에이전트 자기 상태 보고 — <c>POST /edge/v1/status</c>. 운영자가 «이 노드가 얼마나 밀려 있는가» 를 보게 하는 값이고, 판정에는
/// 쓰이지 않는다. 모든 수는 0 이상이며 에이전트의 outbox 가 정본이다(엔진은 받은 값을 그대로 보여 줄 뿐 더하지 않는다).
/// </summary>
public sealed record EdgeStatusReport
{
    /// <summary>아직 엔진에 도착하지 못한 사건 · 크롭 항목 수.</summary>
    [JsonPropertyName("outbox_depth")] public required int OutboxDepth { get; init; }

    /// <summary>outbox 가 디스크에서 차지하는 바이트.</summary>
    [JsonPropertyName("outbox_bytes")] public required long OutboxBytes { get; init; }

    /// <summary>디스크 상한 때문에 지금까지 버린 크롭 수(사건은 버리지 않는다).</summary>
    [JsonPropertyName("dropped_crops")] public required long DroppedCrops { get; init; }

    /// <summary>엔진이 영구 거절해 따로 둔 항목 수 — 사람이 봐야 하는 것.</summary>
    [JsonPropertyName("parked")] public required int Parked { get; init; }

    /// <summary>에이전트 판(선택, 40자 이하) — 현장에 어느 판이 깔렸는지 보는 용도.</summary>
    [JsonPropertyName("agent_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentVersion { get; init; }
}

/// <summary>엔진이 기록한 노드의 마지막 자기 보고 — 보고 내용과 받은 시각.</summary>
public sealed record EdgeNodeStatus
{
    /// <summary>엔진이 이 보고를 받은 시각(UTC ISO 8601) — 노드 시계가 아니라 서버 시계.</summary>
    [JsonPropertyName("reported_at")] public required string ReportedAt { get; init; }

    [JsonPropertyName("outbox_depth")] public required int OutboxDepth { get; init; }

    [JsonPropertyName("outbox_bytes")] public required long OutboxBytes { get; init; }

    [JsonPropertyName("dropped_crops")] public required long DroppedCrops { get; init; }

    [JsonPropertyName("parked")] public required int Parked { get; init; }

    [JsonPropertyName("agent_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentVersion { get; init; }
}
