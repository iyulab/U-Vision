using System.Text.Json.Serialization;

namespace UVision.Api.Models;

// 작은 응답 본문들. 예전엔 익명 객체였다 — 이름이 있어야 OpenAPI 문서가 모양을 적고 생성 클라이언트가 형을 갖는다.
// 속성 순서 · null 직렬화는 익명 객체 때와 같다(wire byte 보존).

/// <summary><c>GET /health</c>.</summary>
public sealed record HealthResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("provider")] public required string Provider { get; init; }

    /// <summary>데이터 볼륨 여유 — 읽을 수 없는 환경이면 생략한다(additive).</summary>
    [JsonPropertyName("data_volume")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DataVolumeStatus? DataVolume { get; init; }
}

/// <summary>데이터 루트가 놓인 볼륨의 여유(<c>GET /health</c>).</summary>
public sealed record DataVolumeStatus
{
    /// <summary>이 프로세스가 쓸 수 있는 여유 바이트(쿼터 반영).</summary>
    [JsonPropertyName("free_bytes")] public required long FreeBytes { get; init; }

    [JsonPropertyName("total_bytes")] public required long TotalBytes { get; init; }

    /// <summary>여유가 <c>Storage:LowSpaceWarningBytes</c> 미만 — 디스크가 가득 차면 저장이 실패한다(판정 기록은 must-succeed).</summary>
    [JsonPropertyName("low")] public required bool Low { get; init; }
}

/// <summary>권한 단계 격상 · 격하 결과.</summary>
public sealed record AuthorityTransitionResponse
{
    [JsonPropertyName("stage")] public required AuthorityStage Stage { get; init; }
    [JsonPropertyName("previous_stage")] public required AuthorityStage PreviousStage { get; init; }
}

/// <summary>라벨 감사 1건의 결과.</summary>
public sealed record AuditResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("prior_label")] public required string PriorLabel { get; init; }
}

/// <summary>모델 버전 등록 결과.</summary>
public sealed record ModelRegisteredResponse
{
    [JsonPropertyName("version")] public required string Version { get; init; }
}

/// <summary>모델 포인터(승격 · 롤백 뒤).</summary>
public sealed record ModelPointerResponse
{
    [JsonPropertyName("active_version")] public string? ActiveVersion { get; init; }
    [JsonPropertyName("previous_version")] public string? PreviousVersion { get; init; }
}

/// <summary>시나리오의 모델 버전 목록과 포인터.</summary>
public sealed record ModelListResponse
{
    [JsonPropertyName("active_version")] public string? ActiveVersion { get; init; }
    [JsonPropertyName("previous_version")] public string? PreviousVersion { get; init; }
    [JsonPropertyName("versions")] public required IReadOnlyList<ModelVersionManifest> Versions { get; init; }
}
