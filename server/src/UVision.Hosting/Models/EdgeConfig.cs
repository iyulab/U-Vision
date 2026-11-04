using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 노드가 받는 구성 — 배정된 시나리오의 <b>투영</b>이다(엔진이 정본, 노드는 읽기 전용 사본을 영속 캐시로 쥐고 오프라인에서도 그것으로 돈다).
/// 엣지가 쓰지 않는 것(VLM 프롬프트 · 기준 이미지 · 클라우드 송출 허용 …)은 보내지 않는다.
/// </summary>
public sealed record EdgeConfig
{
    [JsonPropertyName("node_id")] public required string NodeId { get; init; }

    /// <summary>내용 지문 — <c>ETag</c> 와 같은 값. 구성이 같으면 같고, 엣지가 쓰는 값이나 배정이 바뀔 때만 바뀐다.</summary>
    [JsonPropertyName("version")] public required string Version { get; init; }

    /// <summary>배정된 시나리오의 투영(시나리오 id 순).</summary>
    [JsonPropertyName("scenarios")] public required IReadOnlyList<EdgeScenarioConfig> Scenarios { get; init; }

    /// <summary>배정은 됐지만 시나리오가 지워져 투영할 수 없는 id — 조용히 빼지 않고 이름으로 알린다.</summary>
    [JsonPropertyName("missing_scenarios")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? MissingScenarios { get; init; }
}

/// <summary>
/// 시나리오 한 건의 엣지용 투영. <c>sources</c> · <c>rules</c> · <c>outputs</c> 는 시나리오 어휘에 아직 없다(지어진 값만 받는다 — 워커 계약과
/// 함께 들어온다); 지금 담는 것은 시나리오가 실제로 가진 판정 설정이다.
/// </summary>
public sealed record EdgeScenarioConfig
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("type")] public required string Type { get; init; }

    [JsonPropertyName("trigger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Trigger { get; init; }

    [JsonPropertyName("classes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScenarioClass>? Classes { get; init; }

    [JsonPropertyName("expected_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExpectedCount { get; init; }

    [JsonPropertyName("count_tolerance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CountTolerance { get; init; }

    [JsonPropertyName("expected_by_class")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, int>? ExpectedByClass { get; init; }

    [JsonPropertyName("extra_policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtraPolicy { get; init; }

    [JsonPropertyName("min_confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinConfidence { get; init; }

    [JsonPropertyName("count_classes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? CountClasses { get; init; }
}
