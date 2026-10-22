using System.Text.Json.Serialization;
using HoneAI;

namespace UVision.Api.Models;

/// <summary>
/// 결과 v2 — <c>POST {ApiBasePath}/v2/inspect</c> 의 응답. 판정 값을 시나리오의 클래스 id 로, 누가 확정했는지와
/// 판정 라우터가 단마다 무엇을 했는지를 함께 돌려준다. v1 <c>/inspect</c> 응답은 이 결과의 OK/NG 보기다.
/// 판정할 수 없으면(<see cref="DetectionUnavailable"/>) 클래스 · 확정자 없이 사유와 경로만 온다(HTTP 503).
/// </summary>
public sealed record InspectResultV2
{
    /// <summary>판정 식별자 — 레코드가 있으면 그 <c>image_id</c>, 판정 불가면 장부 항목 id.</summary>
    [JsonPropertyName("decision_id")] public required string DecisionId { get; init; }

    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }

    /// <summary>확정한 클래스 id(<c>OK</c> · <c>NG</c> · …).</summary>
    [JsonPropertyName("class")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Class { get; init; }

    /// <summary>실제로 판정을 낸 단.</summary>
    [JsonPropertyName("settled_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SettledBy? SettledBy { get; init; }

    [JsonPropertyName("confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; init; }

    /// <summary>VLM 소견 — VLM 이 판정을 냈을 때만.</summary>
    [JsonPropertyName("findings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Findings { get; init; }

    /// <summary>사람 검토가 필요한 건(판정원 불일치 · 낮은 신뢰도) — 판정원이 둘일 때만.</summary>
    [JsonPropertyName("requires_review")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequiresReview { get; init; }

    /// <summary>
    /// 판정 라우터의 단별 경로(비용 순). 신뢰 정책이 생기기 전에는 라우터가 관찰만 하므로, 아래 단은 답해도
    /// <c>shadowed</c>(또는 꼭 잡을 값이면 <c>escalated</c>)이고 꼭대기가 <c>settled</c> 다.
    /// </summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PathStepV2>? Path { get; init; }

    [JsonPropertyName("detection_unavailable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DetectionUnavailable { get; init; }

    /// <summary>판정 불가 사유(<see cref="DetectionUnavailable"/> 일 때).</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>판정을 낸 단 — <c>tier</c> 는 시나리오 judges 의 이름, <c>kind</c> 는 판정자 종류.</summary>
public sealed record SettledBy
{
    [JsonPropertyName("tier")] public required string Tier { get; init; }

    [JsonPropertyName("kind")] public required string Kind { get; init; }
}

/// <summary>라우터 경로 한 단 — <c>settled</c> · <c>shadowed</c> · <c>escalated</c> · <c>abstained</c>.</summary>
public sealed record PathStepV2
{
    [JsonPropertyName("tier")] public required string Tier { get; init; }

    [JsonPropertyName("outcome")] public required string Outcome { get; init; }

    /// <summary>HoneAI 경로 → wire 어휘(소문자). enum 이름을 그대로 내보내지 않아 라이브러리 이름 변경이 wire 를 흔들지 않는다.</summary>
    public static IReadOnlyList<PathStepV2>? From(PredictionProvenance? provenance) =>
        provenance?.Path?.Select(s => new PathStepV2
        {
            Tier = s.TierId,
            Outcome = s.Outcome switch
            {
                StepOutcome.Settled => "settled",
                StepOutcome.Shadowed => "shadowed",
                StepOutcome.Escalated => "escalated",
                StepOutcome.Abstained => "abstained",
                var other => other.ToString().ToLowerInvariant(),
            },
        }).ToList();
}
