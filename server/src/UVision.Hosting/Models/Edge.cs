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

    /// <summary>회수된 시각 — 있으면 이 노드의 자격은 더는 통하지 않는다.</summary>
    [JsonPropertyName("revoked_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RevokedAt { get; init; }
}
