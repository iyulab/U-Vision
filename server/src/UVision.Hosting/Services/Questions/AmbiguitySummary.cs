using UVision.Api.Models;

namespace UVision.Api.Services.Questions;

/// <summary>
/// 사람이 «모르겠음» 으로 답한 비율 — 순수 계산(저장소 · HTTP 를 모른다). 문제 기록만 읽는다(사진 · 라벨을 읽지 않는다 — 네트워크 공유에서
/// 라벨 목록 읽기가 비싸다). 출제 이유로 두 집단을 가른다: 무작위로 고른 문제는 시나리오 사진의 모호성을 대표하고, 불확실해서 고른 문제는
/// 어려운 사진에 치우친 표본이라 따로 센다(<see cref="QuestionThreshold.IsRepresentative"/> 와 같은 구분).
/// </summary>
public static class AmbiguitySummary
{
    /// <summary>기권율을 내는 최소 닫힌 문제 수 — 정답률(<see cref="SolverTrustSummary.MinAnswersForRate"/>)과 같은 값이다.</summary>
    public const int MinAnswersForRate = SolverTrustSummary.MinAnswersForRate;

    public static AmbiguityResponse Summarize(string scenarioId, IEnumerable<StoredQuestion> questions)
    {
        var regular = questions.Where(q => q.Source != QuestionSource.Reference).ToList();
        return new AmbiguityResponse
        {
            ScenarioId = scenarioId,
            MinAnswersForRate = MinAnswersForRate,
            Representative = Group(regular.Where(q => QuestionThreshold.IsRepresentative(q.Reason))),
            Targeted = Group(regular.Where(q => !QuestionThreshold.IsRepresentative(q.Reason))),
        };
    }

    private static AmbiguityGroup Group(IEnumerable<StoredQuestion> questions)
    {
        var list = questions.ToList();
        var answered = list.Count(q => q.State == QuestionState.Answered);
        var abstained = list.Count(q => q.State == QuestionState.Abstained);
        return new AmbiguityGroup
        {
            Answered = answered,
            Abstained = abstained,
            Open = list.Count(q => q.State == QuestionState.Open),
            AbstainRate = answered + abstained >= MinAnswersForRate ? (double)abstained / (answered + abstained) : null,
        };
    }
}
