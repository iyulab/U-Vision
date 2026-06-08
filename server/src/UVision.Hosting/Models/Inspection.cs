using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 검사 판정 결과. provider 와 무관한 안정 계약이다.
/// 어떤 VLM 이 뒤에 있든 inspect 는 <see cref="InspectionResult"/> 를 반환한다.
/// (원본: server/app/models/inspection.py)
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Verdict>))]
public enum Verdict
{
    OK,
    NG,
}

/// <summary>
/// 판정에 필요한 시나리오 컨텍스트.
/// 현 단계에서는 criteria(자연어 기준)만 사용한다. 기준 이미지(ok/ng)·ROI 등은
/// 시나리오 관리(Phase 2)에서 확장한다. 여기서 선제 확장하지 않는다(YAGNI).
/// </summary>
public sealed record ScenarioContext
{
    public required string ScenarioId { get; init; }

    public string Name { get; init; } = "";

    public string Criteria { get; init; } = "";

    /// <summary>
    /// few-shot 기준 이미지(OK/NG). 판정 입력의 일부 — criteria 와 함께 provider 에 전달된다.
    /// 비어 있으면 zero-shot(기준 텍스트만). 로드 실패 시에도 비운 채 진행한다(판정은 degrade, 실패 아님).
    /// </summary>
    public IReadOnlyList<ReferenceImage> References { get; init; } = [];
}

/// <summary>
/// VLM 판정 결과 — provider 가 반환하는 도메인 객체.
/// ironhive 구조화 출력(Output=typeof)의 역직렬화 타겟이므로 init 속성으로 둔다.
/// </summary>
public sealed record InspectionResult
{
    public required Verdict Verdict { get; init; }

    /// <summary>NG 시 불량 소견 텍스트.</summary>
    public string Findings { get; init; } = "";

    /// <summary>0.0~1.0. 경계 사례일수록 낮다.</summary>
    public required double Confidence { get; init; }
}

/// <summary>
/// <c>POST /api/inspect</c> 응답 — 도메인 결과 + API 메타데이터.
/// 도메인(<see cref="InspectionResult"/>)과 분리: API 는 timestamp/image_id 를 덧붙인다.
/// wire 계약(snake_case)은 [JsonPropertyName] 으로 자기서술 — ambient JSON 정책 독립.
/// </summary>
public sealed record InspectResponse
{
    [JsonPropertyName("verdict")] public required Verdict Verdict { get; init; }

    [JsonPropertyName("findings")] public required string Findings { get; init; }

    [JsonPropertyName("confidence")] public required double Confidence { get; init; }

    [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }

    [JsonPropertyName("image_id")] public required string ImageId { get; init; }
}

/// <summary>
/// 디스크에 영속화되는 판정 레코드 — <c>{image_id}.json</c> 의 스키마.
/// system-of-record 의 단위. <c>image_file</c> 로 짝 이미지를 가리킨다.
/// <c>{image_id}.json</c> 의 존재 = 완결 레코드(이미지 먼저 쓰고 json 으로 커밋).
/// </summary>
public sealed record StoredResult
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    [JsonPropertyName("verdict")] public required Verdict Verdict { get; init; }

    [JsonPropertyName("findings")] public required string Findings { get; init; }

    [JsonPropertyName("confidence")] public required double Confidence { get; init; }

    [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }

    /// <summary>짝 이미지 파일명(예: <c>img_{guid}.jpg</c>).</summary>
    [JsonPropertyName("image_file")] public required string ImageFile { get; init; }

    /// <summary>촬영 태블릿 안정 식별자(localStorage UUID). 구 레코드/누락 시 "".</summary>
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = "";

    /// <summary>운영자 지정 태블릿 라벨(예: "라인 A 입구"). 표시용. 구 레코드/누락 시 "".</summary>
    [JsonPropertyName("device_label")] public string DeviceLabel { get; init; } = "";
}
