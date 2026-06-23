using System.Text.Json;
using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 권한 이양 사다리 단계(신뢰성 플라이휠 A1) — 서수 정렬(Shadow &lt; Advisory &lt; CoPrimary &lt; MlPrimary).
/// VLM→트리아지 / ML→판정 인수의 점진 이양. 기본(authority.json 없음) = Advisory(오늘 동작).
/// </summary>
[JsonConverter(typeof(AuthorityStageJsonConverter))]
public enum AuthorityStage
{
    /// <summary>ML 실행·메트릭 기록하되 응답에서 ml 생략(침묵 수집). VerdictSource=VLM.</summary>
    Shadow = 0,
    /// <summary>현재 기본 — ML 의견 표시, 불일치=비차단 밴드. VerdictSource=VLM.</summary>
    Advisory = 1,
    /// <summary>불일치=차단형 확인 게이트(ReviewBlock). VerdictSource=VLM.</summary>
    CoPrimary = 2,
    /// <summary>역할 스왑 — VerdictSource=ML, VLM=교차검증.</summary>
    MlPrimary = 3,
}

/// <summary>
/// AuthorityStage ↔ snake_case wire 변환(전용 converter — ambient JSON 정책 없는 아키텍처에 맞춰
/// DTO 자기서술). 알 수 없는/null 값은 Advisory(안전 기본)로 해석.
/// </summary>
public sealed class AuthorityStageJsonConverter : JsonConverter<AuthorityStage>
{
    public override AuthorityStage Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
        Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, AuthorityStage value, JsonSerializerOptions o) =>
        writer.WriteStringValue(ToWire(value));

    public static string ToWire(AuthorityStage s) => s switch
    {
        AuthorityStage.Shadow => "shadow",
        AuthorityStage.Advisory => "advisory",
        AuthorityStage.CoPrimary => "co_primary",
        AuthorityStage.MlPrimary => "ml_primary",
        _ => "advisory",
    };

    public static AuthorityStage Parse(string? wire) => wire switch
    {
        "shadow" => AuthorityStage.Shadow,
        "co_primary" => AuthorityStage.CoPrimary,
        "ml_primary" => AuthorityStage.MlPrimary,
        _ => AuthorityStage.Advisory, // "advisory" + 알 수 없는/null
    };
}

/// <summary>단계 전이 1건(append-only 감사 이력, C1 history 패턴과 동형).</summary>
public sealed record AuthorityTransition
{
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")] public required string To { get; init; }
    [JsonPropertyName("at")] public required string At { get; init; }
    [JsonPropertyName("by")] public string By { get; init; } = "";
    /// <summary>promote | demote-manual | demote-auto.</summary>
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

/// <summary>
/// per-scenario 권한 단계 상태(가변) — <c>{scenario}/authority.json</c>(atomic 교체).
/// B1 <see cref="ModelPointer"/> 포인터 패턴과 동형이되 버전 아티팩트 없음(단계 = enum, YAGNI).
/// 파일 부재 = Advisory(기본) — null 처리는 <see cref="Services.Authority.AuthorityResolver"/>.
/// </summary>
public sealed record AuthorityState
{
    [JsonPropertyName("stage")] public required AuthorityStage Stage { get; init; }
    /// <summary>직전 단계(1-클릭 undo·감사). 최초 설정 시 null.</summary>
    [JsonPropertyName("previous_stage")] public AuthorityStage? PreviousStage { get; init; }
    [JsonPropertyName("updated_at")] public required string UpdatedAt { get; init; }
    [JsonPropertyName("updated_by")] public string UpdatedBy { get; init; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("history")] public IReadOnlyList<AuthorityTransition> History { get; init; } = [];
}

/// <summary><c>POST /scenarios/{id}/authority/promote</c> 요청 본문(wire).</summary>
public sealed record PromoteAuthorityInput
{
    [JsonPropertyName("stage")] public required AuthorityStage Stage { get; init; }
    [JsonPropertyName("by")] public string? By { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

/// <summary><c>POST /scenarios/{id}/authority/demote</c> 요청 본문(wire).</summary>
public sealed record DemoteAuthorityInput
{
    [JsonPropertyName("by")] public string? By { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}
