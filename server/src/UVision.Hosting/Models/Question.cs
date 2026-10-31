using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>문제의 상태 — 단일 출처.</summary>
public static class QuestionState
{
    /// <summary>풀이자에게 나갔고 아직 답이 없다.</summary>
    public const string Open = "open";

    /// <summary>예 / 아니오로 답해 라벨이 됐다.</summary>
    public const string Answered = "answered";

    /// <summary>«모르겠음» — 라벨이 아니라 기권으로 기록됐다.</summary>
    public const string Abstained = "abstained";

    /// <summary>답하기 전에 그 사진에 다른 경로로 라벨이 붙어 거둬들였다 — 라벨을 덮어쓰지 않는다.</summary>
    public const string Withdrawn = "withdrawn";
}

/// <summary>문제 사진의 출처 — 단일 출처. 판정 결과 사진은 값이 없다.</summary>
public static class QuestionSource
{
    /// <summary>정답을 아는 기준 이미지를 심었다(측정용 흐름의 정답 심기).</summary>
    public const string Reference = "reference";
}

/// <summary>문제 형식 — 단일 출처. 지금은 «이 사진은 클래스 중 무엇인가» 하나.</summary>
public static class QuestionForm
{
    public const string Single = "single";
}

/// <summary>
/// 디스크에 영속화되는 문제 한 건 — <c>{scenario}/questions/{question_id}.json</c>. 라벨 사이드카와 분리된 추가 기록이라
/// 지워도 라벨 · 판정은 바뀌지 않는다. 출제 이유와 선택 점수가 남아 라벨이 어떤 경로로 모였는지 나중에 가를 수 있다.
/// </summary>
public sealed record StoredQuestion
{
    [JsonPropertyName("question_id")] public required string QuestionId { get; init; }

    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    /// <summary>묻는 사진의 날짜 버킷 · 이미지 id — 결과 이미지 엔드포인트의 키와 같다.</summary>
    [JsonPropertyName("date")] public required string Date { get; init; }

    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    /// <summary><see cref="QuestionForm"/>.</summary>
    [JsonPropertyName("form")] public required string Form { get; init; }

    /// <summary>이 문제로 모은 라벨의 출제 흐름(<c>learn</c>) — 라벨 이벤트의 표식과 같은 값.</summary>
    [JsonPropertyName("stream")] public required string Stream { get; init; }

    /// <summary>출제 이유(<c>random</c> · <c>uncertain</c> · <c>random_floor</c> · <c>cold_start</c>).</summary>
    [JsonPropertyName("reason")] public required string Reason { get; init; }

    /// <summary>선택 점수(불확실도 — 확률이 아니다). 무작위로 골랐으면 없다.</summary>
    [JsonPropertyName("score")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Score { get; init; }

    /// <summary>
    /// 고르던 순간의 후보 수 — 무작위로 고른 문제(<c>random_floor</c> · <c>cold_start</c> · <c>random</c>)가 그 걸음에서 뽑힐 확률을
    /// 나중에 다시 만들려고 남긴다. 아직 라벨 확인 전의 수라 확률의 근사다. 심은 문제에는 없다.
    /// </summary>
    [JsonPropertyName("pool_size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PoolSize { get; init; }

    /// <summary>고르던 순간 걸음마다 무작위로 낼 몫(0 ~ 1) — 무작위 규칙이거나 심은 문제이면 없다.</summary>
    [JsonPropertyName("random_floor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RandomFloor { get; init; }

    [JsonPropertyName("created_at")] public required string CreatedAt { get; init; }

    /// <summary>
    /// 사진의 출처(<see cref="QuestionSource"/>). 판정 결과 사진이면 없다 — 심은 문제(정답을 아는 기준 이미지)만 적힌다. 서버 안의
    /// 기록이며 풀이자에게 보이는 응답에는 실리지 않는다.
    /// </summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>심은 문제의 기준 이미지 id(<c>references</c> 의 ref id).</summary>
    [JsonPropertyName("source_ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceRef { get; init; }

    /// <summary>심은 문제의 정답 클래스 — 답을 채점할 때만 읽는다(풀이자에게 보이지 않는다).</summary>
    [JsonPropertyName("source_truth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceTruth { get; init; }

    /// <summary>문제를 받은 풀이자(장치 id). 없으면 "".</summary>
    [JsonPropertyName("asked_to")] public required string AskedTo { get; init; }

    /// <summary><see cref="QuestionState"/>.</summary>
    [JsonPropertyName("state")] public required string State { get; init; }

    [JsonPropertyName("closed_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClosedAt { get; init; }

    /// <summary>답한 클래스(<see cref="QuestionState.Answered"/> 일 때).</summary>
    [JsonPropertyName("answer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Answer { get; init; }

    /// <summary>답한 풀이자(장치 id).</summary>
    [JsonPropertyName("answered_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnsweredBy { get; init; }
}

/// <summary><c>GET {api}/questions/next</c> 응답 — 풀이자에게 보이는 문제. 사진은 결과 이미지 엔드포인트로 받는다.</summary>
public sealed record QuestionResponse
{
    [JsonPropertyName("question_id")] public required string QuestionId { get; init; }

    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("date")] public required string Date { get; init; }

    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    [JsonPropertyName("form")] public required string Form { get; init; }

    /// <summary>고를 수 있는 클래스 id — «모르겠음» 은 별도(<see cref="QuestionAnswerInput.Abstain"/>).</summary>
    [JsonPropertyName("choices")] public required IReadOnlyList<string> Choices { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }

    [JsonPropertyName("created_at")] public required string CreatedAt { get; init; }
}

/// <summary><c>POST {api}/questions/answer</c> 요청 본문(snake_case wire) — <c>choice</c> 와 <c>abstain</c> 중 정확히 하나.</summary>
public sealed record QuestionAnswerInput
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("question_id")] public required string QuestionId { get; init; }

    /// <summary>고른 클래스 id.</summary>
    [JsonPropertyName("choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Choice { get; init; }

    /// <summary>«모르겠음» — 라벨이 아니라 기권으로 기록된다.</summary>
    [JsonPropertyName("abstain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Abstain { get; init; }

    /// <summary>풀이자(장치 id). 선택 — 누락 시 "".</summary>
    [JsonPropertyName("by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? By { get; init; }
}

/// <summary>답이 기록된 결과 — <c>recorded</c> 는 <c>label</c> 또는 <c>abstain</c>.</summary>
public sealed record QuestionAnswerResponse
{
    [JsonPropertyName("question_id")] public required string QuestionId { get; init; }

    [JsonPropertyName("recorded")] public required string Recorded { get; init; }
}
