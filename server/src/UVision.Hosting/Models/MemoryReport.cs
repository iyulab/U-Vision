using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 확정 사례 기억의 상태와 문턱 재생 — 관리 화면 · 운영자가 «기억이 지어졌나, 문턱을 어디에 둘까»를 보는 곳.
/// </summary>
public sealed record MemoryReport
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    /// <summary>재생한 날짜(장부 날짜 버킷).</summary>
    [JsonPropertyName("date")] public required string Date { get; init; }

    /// <summary>기억 단이 켜져 있고 이 시나리오의 단에 있는가.</summary>
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }

    /// <summary>기억을 지은 인코더 판(아직 안 지어졌으면 생략).</summary>
    [JsonPropertyName("encoder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Encoder { get; init; }

    /// <summary>기억에 든 확정 사례 수(아직 안 지어졌으면 생략).</summary>
    [JsonPropertyName("cases")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cases { get; init; }

    /// <summary>기억을 마지막으로 지은 시각(UTC, 아직 안 지어졌으면 생략).</summary>
    [JsonPropertyName("built_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuiltAt { get; init; }

    /// <summary>그날 기억이 거리를 남긴 판정 수 — 재생의 모집단.</summary>
    [JsonPropertyName("replayed")] public required int Replayed { get; init; }

    /// <summary>문턱별 재생 결과(작은 문턱부터).</summary>
    [JsonPropertyName("replay")] public required IReadOnlyList<MemoryReplayRow> Replay { get; init; }

    /// <summary>재생한 날 수 — 기본(하루)이면 생략한다. <c>date</c> 가 마지막 날이다.</summary>
    [JsonPropertyName("days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Days { get; init; }

    /// <summary>재생이 있을 때 문턱 추천(없으면 생략). 추천은 제안이다 — 반영은 사람이 한다.</summary>
    [JsonPropertyName("recommendation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MemoryRecommendation? Recommendation { get; init; }
}

/// <summary>
/// 문턱 추천 — 재생 표에서 «사람이 불량이라 확정한 사진이 한 장도 OK 로 지나가지 않은 가장 큰 문턱» 과 그 주장의 증거.
/// 근거가 없으면 <see cref="MaxDistance"/> 가 없고 <see cref="Reason"/> 이 왜인지 말한다.
/// </summary>
public sealed record MemoryRecommendation
{
    /// <summary>추천하는 최근접 코사인 거리 상한(근거가 없으면 생략).</summary>
    [JsonPropertyName("max_distance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxDistance { get; init; }

    /// <summary>그 문턱에서 기억이 답했을 판정 수.</summary>
    [JsonPropertyName("answered")] public required int Answered { get; init; }

    /// <summary>재생한 판정 가운데 사람 라벨이 불량인 수 — 증거의 크기.</summary>
    [JsonPropertyName("labeled_defects")] public required int LabeledDefects { get; init; }

    /// <summary>그 문턱에서 불량이 OK 로 지나간 수(추천은 언제나 0).</summary>
    [JsonPropertyName("missed")] public required int Missed { get; init; }

    /// <summary>불량 통과율의 단측 95 % 상한 — 놓침 0 이어도 불량이 적으면 크게 남는다(근거가 없으면 생략).</summary>
    [JsonPropertyName("defect_pass_upper_95")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DefectPassUpper95 { get; init; }

    /// <summary>그 상한이 목표 놓침률 안인가 — 아니면 추천은 후보일 뿐 증거가 모자라다.</summary>
    [JsonPropertyName("sufficient")] public required bool Sufficient { get; init; }

    /// <summary>목표 놓침률(시나리오 목표값의 누락 상한).</summary>
    [JsonPropertyName("target_miss_rate")] public required double TargetMissRate { get; init; }

    /// <summary>놓침 0 으로 목표를 95 % 신뢰로 주장하는 데 필요한 불량 라벨 수(총).</summary>
    [JsonPropertyName("defects_needed")] public required int DefectsNeeded { get; init; }

    /// <summary>한 문장 설명.</summary>
    [JsonPropertyName("reason")] public required string Reason { get; init; }

    public static MemoryRecommendation From(Services.Memory.OptionAdvice advice, double targetMissRate) => new()
    {
        MaxDistance = advice.MaxDistance,
        Answered = advice.Answered,
        LabeledDefects = advice.LabeledDefects,
        Missed = advice.Missed,
        DefectPassUpper95 = advice.DefectPassUpper95,
        Sufficient = advice.Sufficient,
        TargetMissRate = targetMissRate,
        DefectsNeeded = advice.DefectsNeeded,
        Reason = advice.Reason,
    };
}

/// <summary>문턱 하나에서의 재생 — «이 문턱이었다면».</summary>
public sealed record MemoryReplayRow
{
    /// <summary>최근접 확정 사례의 코사인 거리 상한.</summary>
    [JsonPropertyName("max_distance")] public required double MaxDistance { get; init; }

    /// <summary>지금 설정된 문턱인가.</summary>
    [JsonPropertyName("configured")] public required bool Configured { get; init; }

    /// <summary>기억이 답했을 판정 수.</summary>
    [JsonPropertyName("answered")] public required int Answered { get; init; }

    /// <summary>그 가운데 교사(실제 판정)가 답한 수 — 교사 일치율의 분모.</summary>
    [JsonPropertyName("teacher_checked")] public required int TeacherChecked { get; init; }

    /// <summary>교사와 같은 클래스였던 비율(분모 0 이면 null).</summary>
    [JsonPropertyName("teacher_agreement")] public double? TeacherAgreement { get; init; }

    /// <summary>답한 것 가운데 사람 라벨이 있는 수 — 라벨 일치율 · 놓침률의 분모.</summary>
    [JsonPropertyName("labeled")] public required int Labeled { get; init; }

    /// <summary>사람 라벨과 같은 클래스였던 비율(분모 0 이면 null).</summary>
    [JsonPropertyName("label_agreement")] public double? LabelAgreement { get; init; }

    /// <summary>OK 로 답했는데 사람 라벨이 불량인 수 — 기억이 확정했다면 통과시켰을 오답.</summary>
    [JsonPropertyName("missed")] public required int Missed { get; init; }

    /// <summary>놓침 / 라벨 있는 답(분모 0 이면 null).</summary>
    [JsonPropertyName("miss_rate")] public double? MissRate { get; init; }

    /// <summary>
    /// 재생한 판정 가운데 사람 라벨이 불량(놓치면 안 되는 클래스)인 수 — 문턱과 무관하게 같다. 불량 통과율의 분모.
    /// </summary>
    [JsonPropertyName("labeled_defects")] public required int LabeledDefects { get; init; }

    /// <summary>
    /// 놓침 / 라벨 있는 불량 — «불량 가운데 몇 장을 OK 로 지나가게 했나»(분모 0 이면 null). 놓침률은 분모가 답한 수라 불량이
    /// 드문 현장에서 위험을 작게 보인다.
    /// </summary>
    [JsonPropertyName("defect_pass_rate")] public double? DefectPassRate { get; init; }
}
