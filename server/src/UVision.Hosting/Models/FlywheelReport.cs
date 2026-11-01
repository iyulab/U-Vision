using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 플라이휠 곡선 — 사람이 확정한 라벨이 쌓이는 순서대로 «실제 판정» 과 «확정 사례 기억» 이 사람 라벨과 얼마나 맞았나.
/// 쓸수록 나아진다는 주장의 증거다(곡선 없이 주장하지 않는다). 정답은 사람 확정 라벨뿐이다.
/// </summary>
public sealed record FlywheelReport
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    /// <summary>마지막 날 — 이 날까지 <c>days</c> 일을 읽었다.</summary>
    [JsonPropertyName("date")] public required string Date { get; init; }

    [JsonPropertyName("days")] public required int Days { get; init; }

    /// <summary>구간 하나의 라벨 있는 판정 수.</summary>
    [JsonPropertyName("bucket_size")] public required int BucketSize { get; init; }

    /// <summary>기억이 «답했다» 고 센 거리 상한 — 그 시나리오의 설정 문턱.</summary>
    [JsonPropertyName("memory_max_distance")] public required double MemoryMaxDistance { get; init; }

    /// <summary>읽은 기간에 사람 라벨이 붙은 판정의 총 수.</summary>
    [JsonPropertyName("labeled")] public required int Labeled { get; init; }

    /// <summary>구간별 점 — 라벨이 쌓인 순서(오래된 것부터).</summary>
    [JsonPropertyName("points")] public required IReadOnlyList<FlywheelPoint> Points { get; init; }
}

/// <summary>사람 확정 라벨이 <see cref="LabeledThrough"/> 건 쌓일 때까지의 한 구간. 분모가 0 인 비율은 생략한다(0 으로 꾸미지 않는다).</summary>
public sealed record FlywheelPoint
{
    /// <summary>이 구간 끝까지 쌓인 라벨 있는 판정 수(누적).</summary>
    [JsonPropertyName("labeled_through")] public required int LabeledThrough { get; init; }

    /// <summary>이 구간의 라벨 있는 판정 수 — 작으면 비율이 흔들린다.</summary>
    [JsonPropertyName("judged")] public required int Judged { get; init; }

    /// <summary>실제 판정이 사람 라벨과 같았던 비율.</summary>
    [JsonPropertyName("operative_agreement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OperativeAgreement { get; init; }

    /// <summary>사람 라벨이 불량인 수.</summary>
    [JsonPropertyName("labeled_defects")] public required int LabeledDefects { get; init; }

    /// <summary>그 불량을 실제 판정이 OK 로 지나가게 한 수.</summary>
    [JsonPropertyName("operative_missed")] public required int OperativeMissed { get; init; }

    /// <summary>불량 통과율 = 놓침 / 라벨 있는 불량.</summary>
    [JsonPropertyName("operative_defect_pass")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OperativeDefectPass { get; init; }

    /// <summary>문턱 안에서 기억이 답했을 판정 수.</summary>
    [JsonPropertyName("memory_answered")] public required int MemoryAnswered { get; init; }

    /// <summary>기억이 답한 비율 = 답함 / 이 구간 판정 수.</summary>
    [JsonPropertyName("memory_answered_rate")] public required double MemoryAnsweredRate { get; init; }

    /// <summary>기억의 답이 사람 라벨과 같았던 비율.</summary>
    [JsonPropertyName("memory_agreement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MemoryAgreement { get; init; }

    /// <summary>기억이 OK 로 답했는데 사람 라벨이 불량인 수 — 기억이 확정했다면 통과시켰을 오답.</summary>
    [JsonPropertyName("memory_missed")] public required int MemoryMissed { get; init; }

    /// <summary>전용 ML 이 라벨을 낸 판정 수 — ML 을 켜지 않았거나 분류가 실패한 건은 빠진다.</summary>
    [JsonPropertyName("ml_checked")] public required int MlChecked { get; init; }

    /// <summary>ML 라벨이 사람 라벨과 같았던 비율(ML 이 답한 건이 없으면 생략).</summary>
    [JsonPropertyName("ml_agreement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MlAgreement { get; init; }

    /// <summary>ML 이 OK 로 답했는데 사람 라벨이 불량인 수.</summary>
    [JsonPropertyName("ml_missed")] public required int MlMissed { get; init; }
}
