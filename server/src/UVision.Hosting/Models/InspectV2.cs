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

    /// <summary>운영 자세 — 확인 전까지 다음 캡처를 막아야 할 때만 <c>review_block</c>.</summary>
    [JsonPropertyName("posture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Posture { get; init; }

    /// <summary>
    /// 확정된 클래스와 다른 must-catch 답을 낸 아래 단(예: 장치가 읽은 코드가 기대 품번과 다름) — 있으면 판정은 검토 대기다.
    /// </summary>
    [JsonPropertyName("contested_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ContestedBy>? ContestedBy { get; init; }

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

    // 캡처 출처 — 요청이 보냈을 때만. 이 결과를 그대로 싣는 판정 사건(웹훅)의 받는 쪽이 «어느 장치가 언제 찍었나»를 안다.

    [JsonPropertyName("device_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; init; }

    [JsonPropertyName("device_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceLabel { get; init; }

    /// <summary>장치 캡처 시각(UTC) — <c>timestamp</c> 는 서버가 받은 시각.</summary>
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
}

/// <summary>
/// 요청 맥락 — 시나리오가 아니라 요청마다 달라지는 판정 입력. 오투입 확인(<c>verification</c>)은 기대 품번과,
/// 있으면 촬영 장치(태블릿 · 엣지)가 바코드 · 각인에서 읽은 코드를 함께 보낸다. 서버는 픽셀에서 코드를 다시 읽지 않는다.
/// </summary>
public sealed record InspectionContext
{
    /// <summary>요청 필드 하나의 최대 길이.</summary>
    public const int MaxLength = 128;

    /// <summary>작업지시가 기대하는 품번.</summary>
    public string? ExpectedPart { get; init; }

    /// <summary>장치가 읽은 코드(없으면 대조 단이 기권한다).</summary>
    public string? ObservedCode { get; init; }

    /// <summary>
    /// 경계 검증 — 품번 · 코드에 쓰는 문자(글자 · 숫자 · 공백 · <c>- _ . / # ( ) : +</c>)만 받고 나머지는 거부한다
    /// (변형하지 않는다). 기대 품번은 VLM 프롬프트에 들어가므로 문장 부호 · 줄바꿈으로 지시를 끼워 넣지 못하게 좁힌다.
    /// 위반이면 사유, 아니면 null.
    /// </summary>
    public static string? Validate(string? value, string field) =>
        value is null ? null
        : value.Length > MaxLength ? $"{field} 는 {MaxLength}자 이하"
        : !Allowed.IsMatch(value) ? $"{field} 에 허용하지 않는 문자(글자 · 숫자 · 공백 · - _ . / # ( ) : + 만)"
        : null;

    private static readonly System.Text.RegularExpressions.Regex Allowed =
        new(@"^[\p{L}\p{N} ._/#():+-]*\z", System.Text.RegularExpressions.RegexOptions.Compiled); // \z — $ 는 끝 줄바꿈 앞에서도 맞는다
}

/// <summary>판정을 낸 단 — <c>tier</c> 는 시나리오 judges 의 이름, <c>kind</c> 는 판정자 종류.</summary>
public sealed record SettledBy
{
    [JsonPropertyName("tier")] public required string Tier { get; init; }

    [JsonPropertyName("kind")] public required string Kind { get; init; }
}

/// <summary>확정에 맞선 아래 단의 답 — <c>tier</c> 는 시나리오 judges 의 이름, <c>class</c> 는 그 단이 낸 must-catch 클래스.</summary>
public sealed record ContestedBy
{
    [JsonPropertyName("tier")] public required string Tier { get; init; }

    [JsonPropertyName("class")] public required string Class { get; init; }
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
