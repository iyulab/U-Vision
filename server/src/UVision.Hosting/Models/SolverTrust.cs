using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>풀이자(장치) 한 곳의 정답 심기 결과.</summary>
public sealed record SolverTrustDevice
{
    [JsonPropertyName("device")] public required string Device { get; init; }

    /// <summary>이 장치에 나간 심은 문제 수(열린 것 포함).</summary>
    [JsonPropertyName("planted")] public required int Planted { get; init; }

    [JsonPropertyName("correct")] public required int Correct { get; init; }

    [JsonPropertyName("wrong")] public required int Wrong { get; init; }

    /// <summary>«모르겠음» — 정답도 오답도 아니다.</summary>
    [JsonPropertyName("unsure")] public required int Unsure { get; init; }

    /// <summary>아직 답하지 않은 문제.</summary>
    [JsonPropertyName("open")] public required int Open { get; init; }

    /// <summary>정답률 = 정답 / (정답 + 오답). 답한 건수가 <see cref="SolverTrustResponse.MinAnswersForRate"/> 보다 적으면 없다.</summary>
    [JsonPropertyName("accuracy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Accuracy { get; init; }
}

/// <summary>
/// <c>GET {api}/questions/solver-trust</c> 응답 — 정답을 아는 문제(<c>PlantRate</c>)로 잰 풀이자의 정답률. 심은 문제가 없으면 모두 0 이다.
/// 정답률은 답한 건수가 모자랄 때 내지 않는다 — 몇 건으로 점추정을 내면 풀이자 신뢰로 읽힌다.
/// </summary>
public sealed record SolverTrustResponse
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("planted")] public required int Planted { get; init; }

    [JsonPropertyName("correct")] public required int Correct { get; init; }

    [JsonPropertyName("wrong")] public required int Wrong { get; init; }

    [JsonPropertyName("unsure")] public required int Unsure { get; init; }

    [JsonPropertyName("open")] public required int Open { get; init; }

    /// <summary>정답률을 내는 데 필요한 답한 건수(정답 + 오답)의 최소값.</summary>
    [JsonPropertyName("min_answers_for_rate")] public required int MinAnswersForRate { get; init; }

    [JsonPropertyName("accuracy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Accuracy { get; init; }

    [JsonPropertyName("devices")] public required IReadOnlyList<SolverTrustDevice> Devices { get; init; }
}
