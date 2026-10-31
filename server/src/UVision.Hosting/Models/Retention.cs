using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// <c>POST {api}/scenarios/{id}/retention/purge</c> 요청 본문 — 설치자가 날짜 구간과 부류를 이름으로 골라 지운다.
/// 기본은 계획만(<c>dry_run</c> 생략 = true)이고, 실제 삭제는 <c>dry_run: false</c> 와 바로 직전 계획의 지문을 함께 보내야 한다.
/// </summary>
public sealed record RetentionPurgeInput
{
    /// <summary>구간 시작 날짜 <c>yyyy-MM-dd</c>(포함).</summary>
    [JsonPropertyName("from")] public required string From { get; init; }

    /// <summary>구간 끝 날짜 <c>yyyy-MM-dd</c>(포함). 오늘(UTC) 이후는 받지 않는다 — 진행 중인 날을 지우지 않는다.</summary>
    [JsonPropertyName("to")] public required string To { get; init; }

    /// <summary>지울 정본 부류: <c>records</c>(사진 · 레코드 · 라벨) · <c>ledger</c> · <c>metrics</c>. 이름에 없는 것은 지우지 않는다.</summary>
    [JsonPropertyName("kinds")] public required IReadOnlyList<string> Kinds { get; init; }

    /// <summary>생략 = true(계획만, 아무것도 지우지 않는다). 지우려면 false 를 명시한다.</summary>
    [JsonPropertyName("dry_run")] public bool? DryRun { get; init; }

    /// <summary>실제 삭제에 필요 — 바로 직전 같은 구간 · 부류의 계획 응답이 돌려준 <c>plan_fingerprint</c>.</summary>
    [JsonPropertyName("plan_fingerprint")] public string? PlanFingerprint { get; init; }
}

/// <summary>부류 하나의 지울(또는 지운) 크기.</summary>
public sealed record RetentionKindSummary
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    /// <summary>해당 날짜 수.</summary>
    [JsonPropertyName("days")] public required int Days { get; init; }

    [JsonPropertyName("files")] public required int Files { get; init; }

    [JsonPropertyName("bytes")] public required long Bytes { get; init; }
}

/// <summary>지우면 잃는 것 — 되돌릴 수 없으니 실제 삭제 전에 읽는 숫자다. 고르지 않은 부류의 칸은 0.</summary>
public sealed record RetentionLoss
{
    /// <summary>판정 레코드 수(<c>records</c>).</summary>
    [JsonPropertyName("results")] public required int Results { get; init; }

    /// <summary>사람이 확정한 라벨 수(<c>records</c>) — 학습 · 감사 · 기억의 정답이다.</summary>
    [JsonPropertyName("human_labels")] public required int HumanLabels { get; init; }

    /// <summary>그 가운데 어느 데이터셋 export 에도 담기지 않은 라벨 수 — 지우면 학습 정답으로 남는 곳이 없다.</summary>
    [JsonPropertyName("labels_not_exported")] public required int LabelsNotExported { get; init; }

    /// <summary>구간 안 사진을 가리키는 열린 문제 수(<c>records</c>) — 사진이 없어져 풀 수 없으므로 삭제가 철회한다.</summary>
    [JsonPropertyName("open_questions")] public required int OpenQuestions { get; init; }

    /// <summary>결정 장부의 결정 수(<c>ledger</c>) — 관문 재측정의 원천이다.</summary>
    [JsonPropertyName("decisions")] public required int Decisions { get; init; }

    /// <summary>메트릭 행 수(<c>metrics</c>).</summary>
    [JsonPropertyName("metric_rows")] public required int MetricRows { get; init; }
}

/// <summary>
/// 보존 삭제 응답 — 계획이든 실제 삭제든 같은 모양이다. <c>plan_fingerprint</c> 는 파일 목록 · 크기의 해시라 계획과 삭제 사이에
/// 구간 안이 달라졌으면(라벨이 더해졌다 등) 달라지고, 그 지문으로는 지워지지 않는다.
/// </summary>
public sealed record RetentionPurgeResponse
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("from")] public required string From { get; init; }

    [JsonPropertyName("to")] public required string To { get; init; }

    [JsonPropertyName("dry_run")] public required bool DryRun { get; init; }

    /// <summary>실제로 지웠나(<c>dry_run</c> 이 false 이고 끝까지 마쳤을 때 true).</summary>
    [JsonPropertyName("purged")] public required bool Purged { get; init; }

    /// <summary>이 계획의 지문 — 실제 삭제 요청이 되돌려 보낸다.</summary>
    [JsonPropertyName("plan_fingerprint")] public required string PlanFingerprint { get; init; }

    [JsonPropertyName("kinds")] public required IReadOnlyList<RetentionKindSummary> Kinds { get; init; }

    [JsonPropertyName("lost")] public required RetentionLoss Lost { get; init; }

    /// <summary>
    /// <c>records</c> 를 골랐을 때만 — 구간 안 사람 라벨이 모두 어느 데이터셋 export 에 담겼나. false 면 지우기 전에 export 를 권한다
    /// (막지는 않는다 — 설치자가 정한다). 라벨이 없으면 true.
    /// </summary>
    [JsonPropertyName("covered_by_export")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CoveredByExport { get; init; }
}
