using UVision.Api.Models;

namespace UVision.Api.Services.Questions;

/// <summary>
/// 심은 문제의 결과를 장치별로 센다 — 순수 계산(저장소 · HTTP 를 모른다). 문제 기록이 정답(<c>source_truth</c>) · 답 · 답한 장치를 모두 가지므로
/// 문제 저장소만 읽는다(정답 심기 기록은 풀이자 키를 싣지 않는다).
/// </summary>
public static class SolverTrustSummary
{
    /// <summary>정답률을 내는 최소 답한 건수 — 이보다 적으면 정답률은 없다(건수만).</summary>
    public const int MinAnswersForRate = 10;

    public static SolverTrustResponse Summarize(string scenarioId, IEnumerable<StoredQuestion> questions)
    {
        var devices = questions
            .Where(q => q.Source == QuestionSource.Reference)
            .GroupBy(q => q.State == QuestionState.Open ? q.AskedTo : q.AnsweredBy ?? q.AskedTo, StringComparer.Ordinal)
            .Select(g => Device(g.Key, g.ToList()))
            .OrderByDescending(d => d.Planted).ThenBy(d => d.Device, StringComparer.Ordinal)
            .ToList();

        var correct = devices.Sum(d => d.Correct);
        var wrong = devices.Sum(d => d.Wrong);
        return new SolverTrustResponse
        {
            ScenarioId = scenarioId,
            Planted = devices.Sum(d => d.Planted),
            Correct = correct,
            Wrong = wrong,
            Unsure = devices.Sum(d => d.Unsure),
            Open = devices.Sum(d => d.Open),
            MinAnswersForRate = MinAnswersForRate,
            Accuracy = Rate(correct, wrong),
            Devices = devices,
        };
    }

    private static SolverTrustDevice Device(string device, List<StoredQuestion> questions)
    {
        var correct = questions.Count(q => q.State == QuestionState.Answered
            && string.Equals(q.Answer, q.SourceTruth, StringComparison.OrdinalIgnoreCase));
        var wrong = questions.Count(q => q.State == QuestionState.Answered
            && !string.Equals(q.Answer, q.SourceTruth, StringComparison.OrdinalIgnoreCase));
        return new SolverTrustDevice
        {
            Device = device,
            Planted = questions.Count(q => q.State is QuestionState.Open or QuestionState.Answered or QuestionState.Abstained),
            Correct = correct,
            Wrong = wrong,
            Unsure = questions.Count(q => q.State == QuestionState.Abstained),
            Open = questions.Count(q => q.State == QuestionState.Open),
            Accuracy = Rate(correct, wrong),
        };
    }

    private static double? Rate(int correct, int wrong) =>
        correct + wrong >= MinAnswersForRate ? (double)correct / (correct + wrong) : null;
}
