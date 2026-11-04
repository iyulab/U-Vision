using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 상위 단 재판정 요청 — 저장된 판정 한 건에 사용자가 고른 판정원을 지금 다시(또는 더 센 판정원으로) 물어본다.
/// </summary>
public sealed record RejudgeRequest
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    /// <summary>판정이 든 날짜 폴더(<c>yyyy-MM-dd</c>).</summary>
    [JsonPropertyName("date")] public required string Date { get; init; }

    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    /// <summary>물어볼 판정원 — <c>vlm</c>(현재 판으로 다시) · <c>oracle</c>(더 센 2차 의견 VLM) · <c>ml</c> · <c>memory</c>.</summary>
    [JsonPropertyName("tiers")] public required IReadOnlyList<string> Tiers { get; init; }
}

/// <summary>재판정에서 한 판정원이 낸 답 — 또는 기권(사유와 함께).</summary>
public sealed record RejudgeOpinion
{
    /// <summary>요청한 판정원 이름(<c>vlm</c> · <c>oracle</c> · <c>ml</c> · <c>memory</c>).</summary>
    [JsonPropertyName("tier")] public required string Tier { get; init; }

    /// <summary>판정자 종류 — <c>vlm</c> · <c>ml</c> · <c>system1</c>.</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    /// <summary><c>answered</c> | <c>abstained</c>.</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>시나리오가 선언한 클래스 — 기권이면 없다.</summary>
    [JsonPropertyName("class")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Class { get; init; }

    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; init; }

    [JsonPropertyName("findings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Findings { get; init; }

    /// <summary>이 답을 낸 판(모델 · 프롬프트 · 기준 이미지 지문 등) — 원 판정의 <c>tier_version</c> 과 견줘 «같은 판인가» 를 본다.</summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    /// <summary>기권 사유 · 못 부른 이유 — 답했으면 없다.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>원 판정의 클래스와 다른가 — 기권이면 없다.</summary>
    [JsonPropertyName("differs_from_original")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DiffersFromOriginal { get; init; }
}

/// <summary>재판정의 결정적 요약 — 문장이 아니라 코드와 목록(표시 말투는 보는 쪽이 정한다).</summary>
public sealed record RejudgeSynthesis
{
    /// <summary><c>unanimous</c>(원 판정과 답한 판정원 모두 같은 클래스) | <c>split</c>(갈림) | <c>none_answered</c>(아무도 답하지 못함).</summary>
    [JsonPropertyName("agreement")] public required string Agreement { get; init; }

    /// <summary>원 판정과 다른 클래스를 낸 판정원.</summary>
    [JsonPropertyName("differing_tiers")] public required IReadOnlyList<string> DifferingTiers { get; init; }

    /// <summary>답한 판정원 수.</summary>
    [JsonPropertyName("answered")] public required int Answered { get; init; }

    /// <summary>클래스별 표 수 — 원 판정을 한 표로 센다.</summary>
    [JsonPropertyName("votes")] public required IReadOnlyDictionary<string, int> Votes { get; init; }
}

/// <summary>
/// 재판정 결과 — 원 판정과 새 소견을 나란히. <b>저장된 판정 · 메트릭 · 결정 장부를 바꾸지 않는다</b>(별도 기록).
/// 원 판정은 사람이 라벨로 정할 때까지 그대로다.
/// </summary>
public sealed record RejudgeResponse
{
    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("date")] public required string Date { get; init; }

    /// <summary>재판정을 요청한 시각(서버 UTC, ISO 8601).</summary>
    [JsonPropertyName("requested_at")] public required string RequestedAt { get; init; }

    /// <summary>원 판정의 클래스.</summary>
    [JsonPropertyName("original_class")] public required string OriginalClass { get; init; }

    /// <summary>원 판정을 낸 단의 판 — 결정 장부가 있을 때만.</summary>
    [JsonPropertyName("original_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalVersion { get; init; }

    [JsonPropertyName("opinions")] public required IReadOnlyList<RejudgeOpinion> Opinions { get; init; }

    [JsonPropertyName("synthesis")] public required RejudgeSynthesis Synthesis { get; init; }
}
