using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 모델 버전의 불변 참조·이력 레코드(신뢰성 플라이휠 B1). 모델 바이너리는 MLoop serve 가 소유하고,
/// 여기서는 그것을 가리키는 <b>레시피</b>(model_name + 학습 데이터 provenance + 격상 메트릭)만 버전한다.
/// 디스크: <c>{scenario}/models/{version}/manifest.json</c>.
/// </summary>
public sealed record ModelVersionManifest
{
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    /// <summary>mloop serve 호출명(<c>/predict?name=</c>). 버전마다 유일해야 추적성 성립.</summary>
    [JsonPropertyName("model_name")] public required string ModelName { get; init; }

    /// <summary>provenance 기록용. B1 해석은 전역 MlOptions.Endpoint 사용(per-scenario override 는 YAGNI).</summary>
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }

    /// <summary>학습 데이터 출처(→ datasets/{export_id}, 재현성). 콜드스타트·수동 등록 시 null.</summary>
    [JsonPropertyName("export_id")] public string? ExportId { get; init; }

    /// <summary>격상 시 B3 메트릭 스냅샷(예: ng_recall). 선택.</summary>
    [JsonPropertyName("metrics")] public IReadOnlyDictionary<string, double>? Metrics { get; init; }

    /// <summary>등록 시각. ISO-8601 UTC.</summary>
    [JsonPropertyName("created_at")] public required string CreatedAt { get; init; }

    /// <summary>등록한 태블릿 식별자(cycle-35 device UUID 재사용). 누락 시 "".</summary>
    [JsonPropertyName("created_by")] public string CreatedBy { get; init; } = "";

    [JsonPropertyName("note")] public string? Note { get; init; }
}

/// <summary>
/// active 모델 포인터(가변). promote 가 교체하고 rollback 이 <c>active↔previous</c> 를 스왑한다.
/// 디스크: <c>{scenario}/models/active.json</c>(atomic 교체).
/// </summary>
public sealed record ModelPointer
{
    [JsonPropertyName("active_version")] public required string ActiveVersion { get; init; }

    /// <summary>직전 active(1-클릭 rollback 지원). 최초 promote 시 null.</summary>
    [JsonPropertyName("previous_version")] public string? PreviousVersion { get; init; }

    [JsonPropertyName("updated_at")] public required string UpdatedAt { get; init; }

    [JsonPropertyName("updated_by")] public string UpdatedBy { get; init; } = "";
}

/// <summary>레지스트리 register 입력(서비스 경계 — wire 타입 아님).</summary>
public sealed record ModelRegistration
{
    public required string ModelName { get; init; }
    public string? ExportId { get; init; }
    public string? Endpoint { get; init; }
    public IReadOnlyDictionary<string, double>? Metrics { get; init; }
    public string? Note { get; init; }
    public string By { get; init; } = "";
}

/// <summary><c>POST /scenarios/{id}/models</c> 요청 본문(wire, snake_case).</summary>
public sealed record RegisterModelInput
{
    [JsonPropertyName("model_name")] public required string ModelName { get; init; }
    [JsonPropertyName("export_id")] public string? ExportId { get; init; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }
    [JsonPropertyName("metrics")] public IReadOnlyDictionary<string, double>? Metrics { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("by")] public string? By { get; init; }
}

/// <summary>해석된 active 바인딩 — inspect 핫패스가 소비(version=추적성, model_name=mloop 호출).</summary>
public sealed record ModelBinding
{
    public required string Version { get; init; }
    public required string ModelName { get; init; }
}
