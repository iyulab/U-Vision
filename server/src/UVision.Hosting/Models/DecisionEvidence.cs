using System.Text.Json.Serialization;
using HoneAI;

namespace UVision.Api.Models;

/// <summary>
/// 판정 근거 — 저장된 판정 한 건을 «누가 · 어떤 경로로 · 무엇을 근거로» 낸 것인지 레코드와 결정 장부에서 모아 돌려주는
/// 읽기 전용 보기(<c>GET /results/decision</c>). 새로 판정하거나 어떤 판정원도 다시 부르지 않는다.
/// <para>
/// 장부는 관찰 전용이고 쓰기 실패가 판정을 막지 않으므로(degrade) 없을 수 있다 — 없으면
/// <see cref="LedgerRecorded"/> 가 false 이고 경로 · 확정자는 <b>생략</b>한다(지어내지 않는다).
/// </para>
/// </summary>
public sealed record DecisionEvidence
{
    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    /// <summary>판정이 든 날짜 폴더(<c>yyyy-MM-dd</c>) — 조회에 쓴 값 그대로.</summary>
    [JsonPropertyName("date")] public required string Date { get; init; }

    /// <summary>판정한 클래스 — 불량 유형이 있으면 그것, 아니면 OK/NG.</summary>
    [JsonPropertyName("class")] public required string Class { get; init; }

    [JsonPropertyName("confidence")] public required double Confidence { get; init; }

    [JsonPropertyName("findings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Findings { get; init; }

    [JsonPropertyName("requires_review")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequiresReview { get; init; }

    [JsonPropertyName("posture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Posture { get; init; }

    /// <summary>VLM · ML 일치 여부 — 판정원이 둘이었을 때만.</summary>
    [JsonPropertyName("agreement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Agreement { get; init; }

    /// <summary>전용 ML 이 낸 답 — 레코드의 ML 결과(아래 단이 무엇이라 답했는지의 정본).</summary>
    [JsonPropertyName("ml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MlResult? Ml { get; init; }

    /// <summary>계수 판정의 근거(개수 · 상자) — 계수 시나리오에서만.</summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CountInfo? Count { get; init; }

    /// <summary>결정 장부에 이 판정의 줄이 있었는가. false 면 아래 장부 유래 필드는 모두 없다.</summary>
    [JsonPropertyName("ledger_recorded")] public required bool LedgerRecorded { get; init; }

    [JsonPropertyName("settled_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SettledBy? SettledBy { get; init; }

    /// <summary>확정한 단의 판(모델 · 프롬프트 · 기준 이미지 지문) — 같은 판끼리 비교하려는 읽기용.</summary>
    [JsonPropertyName("tier_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TierVersion { get; init; }

    /// <summary>라우터가 단마다 한 일 — 아래 단 먼저, 꼭대기 마지막.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EvidenceStep>? Path { get; init; }

    /// <summary>사람이 정한 라벨(현재 효력이 있는 것) — 라벨이 없으면 생략. 이 판정을 바로잡는 유일한 길이다.</summary>
    [JsonPropertyName("human_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HumanLabel { get; init; }

    /// <summary>오라클(더 센 VLM)의 가장 최근 2차 소견 — 스윕이 남긴 것. 판정을 바꾸지 않는 비operative 소견이다.</summary>
    [JsonPropertyName("oracle_opinion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OracleOpinion { get; init; }

    /// <summary>사용자가 요청한 재판정들(요청 시각 순) — 없으면 생략. 별도 기록에서 읽으며 판정에는 쓰이지 않았다.</summary>
    [JsonPropertyName("rejudges")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RejudgeResponse>? Rejudges { get; init; }

    /// <summary>
    /// 장부 annotation 전부(이름순) — 실제로 쓴 결정(<c>operative:*</c>), 기억 단의 거리 · 이웃(<c>memory:*</c>),
    /// 계수 근거(<c>count:*</c>) 등. 어휘는 처리 코드와 같은 판에서 늘어난다.
    /// </summary>
    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }
}

/// <summary>
/// 근거 보기의 경로 한 단 — 결과 v2 의 <see cref="PathStepV2"/>(동결된 wire)에 단의 종류와 단이 남긴 사유를 더했다.
/// </summary>
public sealed record EvidenceStep
{
    [JsonPropertyName("tier")] public required string Tier { get; init; }

    /// <summary>판정자 종류 — <c>system1</c> · <c>ml</c> · <c>vlm</c> · <c>human</c>.</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    [JsonPropertyName("outcome")] public required string Outcome { get; init; }

    /// <summary>단이 남긴 사유(기권 이유 등) — 없으면 생략.</summary>
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    internal static EvidenceStep From(TierStep step) => new()
    {
        Tier = step.TierId,
        Kind = step.Kind.ToString().ToLowerInvariant(),
        Outcome = PathStepV2.OutcomeName(step.Outcome),
        Detail = string.IsNullOrEmpty(step.Detail) ? null : step.Detail,
    };
}
