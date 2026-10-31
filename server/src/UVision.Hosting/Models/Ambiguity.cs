using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>한 출제 집단의 풀이 결과 — 답한 건 · «모르겠음» 건 · 기권율.</summary>
public sealed record AmbiguityGroup
{
    /// <summary>사진을 보고 클래스를 고른 문제.</summary>
    [JsonPropertyName("answered")] public required int Answered { get; init; }

    /// <summary>«모르겠음» 으로 닫힌 문제 — 사진이 모호하거나 클래스 정의가 모호하다는 신호(라벨이 아니다).</summary>
    [JsonPropertyName("abstained")] public required int Abstained { get; init; }

    /// <summary>아직 답하지 않은 문제.</summary>
    [JsonPropertyName("open")] public required int Open { get; init; }

    /// <summary>
    /// 기권율 = 기권 / (답 + 기권). 닫힌 문제가 <see cref="AmbiguityResponse.MinAnswersForRate"/> 보다 적으면 없다 — 몇 건으로 점추정을
    /// 내면 시나리오의 모호성으로 읽힌다.
    /// </summary>
    [JsonPropertyName("abstain_rate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AbstainRate { get; init; }
}

/// <summary>
/// <c>GET {api}/questions/ambiguity</c> 응답 — 사람이 «모르겠음» 으로 답한 비율. 두 집단을 섞지 않는다: <see cref="Representative"/> 는 후보
/// 전체에서 무작위로 고른 문제(그 시나리오 사진의 모호성을 대표한다), <see cref="Targeted"/> 는 기억 단이 헷갈린 사진을 골라 낸 문제(일부러
/// 어려운 것을 모았으므로 기권율이 높은 쪽으로 치우친다 — 시나리오의 모호성이 아니다). 정답을 아는 심은 문제는 풀이자 신뢰의 몫이라 세지 않는다.
/// </summary>
public sealed record AmbiguityResponse
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    /// <summary>기권율을 내는 데 필요한 닫힌 문제(답 + 기권)의 최소값.</summary>
    [JsonPropertyName("min_answers_for_rate")] public required int MinAnswersForRate { get; init; }

    [JsonPropertyName("representative")] public required AmbiguityGroup Representative { get; init; }

    [JsonPropertyName("targeted")] public required AmbiguityGroup Targeted { get; init; }
}
