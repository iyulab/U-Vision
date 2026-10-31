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

    /// <summary>오투입 확인의 기대 품번 — 있으면 VLM 이 «이 부품이 기대 품번인가»를 OK/NG 로 답한다.</summary>
    public string? ExpectedPart { get; init; }

    /// <summary>
    /// NG 를 나눌 불량 유형 — 시나리오가 유형을 선언했을 때만. 있으면 VLM 이 NG 에 유형 id 하나를 댄다(표시 이름은
    /// 프롬프트가 유형을 설명하는 데만 쓴다).
    /// </summary>
    public IReadOnlyList<ScenarioClass>? DefectClasses { get; init; }
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

    /// <summary>
    /// NG 의 불량 유형 id — 시나리오가 유형을 선언했을 때만(<see cref="ScenarioContext.DefectClasses"/>). 이 타입은 이분
    /// 판정의 구조화 출력 스키마이기도 해서 스키마에서 뺀다 — 유형은 다른 출력 형태로 받는다.
    /// </summary>
    [JsonIgnore]
    public string? DefectClass { get; init; }
}

/// <summary>
/// 전용 ML 분류 결과 — 2중체크(③) wire 의 ML 측. <c>InspectResponse.Ml</c>/<c>StoredResult.Ml</c> 에 중첩.
/// class-agnostic 라벨(다중분류 대비) + 신뢰도. ML 비활성(기본 none)이면 부모에서 생략된다.
/// </summary>
public sealed record MlResult
{
    [JsonPropertyName("label")] public required string Label { get; init; }

    [JsonPropertyName("confidence")] public required double Confidence { get; init; }

    /// <summary>판정 모델 버전(B1, mloop active 바인딩 시). 미바인딩/none/mock 이면 null → 생략.</summary>
    [JsonPropertyName("model_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelVersion { get; init; }
}

/// <summary>
/// <c>POST /api/inspect</c> 응답 — 도메인 결과 + API 메타데이터.
/// 도메인(<see cref="InspectionResult"/>)과 분리: API 는 timestamp/image_id 를 덧붙인다.
/// wire 계약(snake_case)은 [JsonPropertyName] 으로 자기서술 — ambient JSON 정책 독립.
/// <para>
/// ③ 2중체크 필드(<c>ml</c>/<c>agreement</c>/<c>requires_review</c>)는 <b>additive·nullable</b> —
/// ML 비활성(기본 none) 시 직렬화에서 생략되어 응답이 ② 이전과 <b>byte-identical</b>(wire 계약 보존).
/// </para>
/// </summary>
public sealed record InspectResponse
{
    [JsonPropertyName("verdict")] public required Verdict Verdict { get; init; }

    [JsonPropertyName("findings")] public required string Findings { get; init; }

    [JsonPropertyName("confidence")] public required double Confidence { get; init; }

    [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }

    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    /// <summary>전용 ML 교차검증 결과(③). ML 비활성/실패 시 null → 생략.</summary>
    [JsonPropertyName("ml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MlResult? Ml { get; init; }

    /// <summary>VLM·ML 판정 일치 여부(③). ML 없으면 null → 생략.</summary>
    [JsonPropertyName("agreement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Agreement { get; init; }

    /// <summary>불일치/저신뢰 → 사람·오라클 검토 필요(③→④). ML 없으면 null → 생략.</summary>
    [JsonPropertyName("requires_review")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequiresReview { get; init; }

    /// <summary>운영 자세(A1) — 차단형 확인 게이트일 때만 "review_block". 그 외 생략(advisory byte-identical).</summary>
    [JsonPropertyName("posture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Posture { get; init; }
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

    // --- ③ 2중체크 — additive·nullable. ML 비활성/실패 시 생략(구 레코드와 동일 형태). ---

    /// <summary>전용 ML 교차검증 결과(③). ML 비활성/실패 시 null → 생략.</summary>
    [JsonPropertyName("ml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MlResult? Ml { get; init; }

    /// <summary>VLM·ML 판정 일치 여부(③). ML 없으면 null → 생략.</summary>
    [JsonPropertyName("agreement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Agreement { get; init; }

    /// <summary>불일치/저신뢰 → 검토 필요(③→④). ML 없으면 null → 생략.</summary>
    [JsonPropertyName("requires_review")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequiresReview { get; init; }

    /// <summary>운영 자세(A1) — 차단형 확인 게이트일 때만 "review_block". 그 외 생략(advisory byte-identical).</summary>
    [JsonPropertyName("posture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Posture { get; init; }

    /// <summary>오투입 확인의 기대 품번 — 요청이 보냈을 때만(감사: 무엇과 대조했나).</summary>
    [JsonPropertyName("expected_part")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedPart { get; init; }

    /// <summary>장치가 읽은 코드 — 요청이 보냈을 때만.</summary>
    [JsonPropertyName("observed_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservedCode { get; init; }

    /// <summary>
    /// 장치가 캡처한 시각(UTC ISO 8601) — 요청이 보냈을 때만. 오프라인 큐로 늦게 올라온 캡처의 실제 시각이다.
    /// <c>timestamp</c> · 날짜 디렉터리는 그대로 서버가 받은 시각(메트릭 · 장부가 그 시각에 묶인다).
    /// </summary>
    [JsonPropertyName("captured_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CapturedAt { get; init; }

    /// <summary>캡처 출처 — 시험 데이터셋에서 넣은 판정이면 <c>dataset</c>(<see cref="CaptureOrigin"/>). 현장 촬영이면 생략.</summary>
    [JsonPropertyName("origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Origin { get; init; }

    /// <summary>출처 안의 표본 식별 — 요청이 보냈을 때만.</summary>
    [JsonPropertyName("origin_ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginRef { get; init; }

    /// <summary>
    /// 판정한 클래스(불량 유형) — 시나리오가 NG 를 유형으로 나눌 때만. 그 밖에는 <c>verdict</c> 가 곧 클래스라 생략한다.
    /// </summary>
    [JsonPropertyName("class")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Class { get; init; }

    /// <summary>계수 시나리오의 판정 근거(센 개수 · 상자) — 계수가 아니면 생략한다.</summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CountInfo? Count { get; init; }
}

/// <summary>
/// <c>POST /api/inspect</c> fail-closed(503) 본문 — 주 검출원(VLM) 사용 불가 시 반환(③.5 E2).
/// 성공(200) 응답과 별개 타입 — 성공 wire 는 byte-identical 보존, 503 은 현재 미정의 500 자리를 정의한다.
/// <para>
/// <c>ml_hint</c> 는 VLM-down·ML-up 일 때 ML 의견을 advisory 로 싣는다(verdict 아님 — 사람 확인 보조).
/// 에러 본문이므로 안정 계약이 아니다(additive·nullable).
/// </para>
/// </summary>
public sealed record DetectionUnavailableResponse
{
    [JsonPropertyName("detection_unavailable")] public bool DetectionUnavailable { get; init; } = true;

    /// <summary>fail-closed 사유(예: "vlm_unavailable") — 진단·클라 표시용.</summary>
    [JsonPropertyName("reason")] public required string Reason { get; init; }

    /// <summary>ML 참고 의견(VLM-down·ML-up 시에만). verdict 아님.</summary>
    [JsonPropertyName("ml_hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MlResult? MlHint { get; init; }
}
