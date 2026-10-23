using UVision.Api.Models;
using UVision.Api.Services.Scenarios;

namespace UVision.Api.Services.Inspection;

/// <summary>
/// must-catch 충돌 원칙 — 아래 단이 놓치면 안 되는 값(<c>must_catch</c>)을 답했는데 위 단이 다른 클래스로 확정했다면,
/// 그 판정은 확정이 아니라 사람 검토 대기다. 위 단은 must-catch 를 확인할 수는 있어도, 뒤집을 때는 사람이 본다 —
/// 가장 강한 증거(결정론적 대조)가 가장 약한 답에 조용히 지지 않게 한다.
/// <para>
/// 말할 권한이 있는 아래 단만 센다. 대조 단(<c>match</c>)은 결정론적이라 쌓을 신뢰가 없어 언제나 센다. 전용 ML 의 답은
/// 권한 단계가 신뢰를 정하고, 그 답이 드러나는 단계에서는 2중체크가 불일치를 이미 검토로 올리므로 여기서 다시 보지 않는다.
/// </para>
/// </summary>
public static class MustCatchGuard
{
    /// <summary>
    /// 확정된 클래스 <paramref name="decidedClass"/> 와 다른 must-catch 답을 낸 아래 단들 — 비었으면 충돌 없음.
    /// </summary>
    public static IReadOnlyList<ContestedBy> Contests(
        EffectiveScenario scenario, InspectionContext? context, string decidedClass)
    {
        List<ContestedBy> contests = [];
        foreach (var tier in scenario.Tiers)
        {
            var answer = tier == ScenarioVocabulary.MatchTier ? MatchJudge.Answer(context) : null;
            if (answer is not null
                && scenario.Classes.Any(c => c.MustCatch && string.Equals(c.Id, answer, StringComparison.OrdinalIgnoreCase))
                && !string.Equals(answer, decidedClass, StringComparison.OrdinalIgnoreCase))
                contests.Add(new ContestedBy { Tier = tier, Class = answer });
        }
        return contests;
    }
}

/// <summary>
/// 결정론적 대조 — 장치가 읽은 코드와 기대 품번이 같으면 일치, 다르면 불일치. 코드가 없으면 기권(null).
/// 결정 장부의 대조 단과 충돌 원칙이 같은 답을 보도록 한 곳에 둔다.
/// </summary>
public static class MatchJudge
{
    public static string? Answer(InspectionContext? context) =>
        context is { ObservedCode: { } observed, ExpectedPart: { } expected }
            ? string.Equals(observed.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase)
                ? ScenarioVocabulary.Match
                : ScenarioVocabulary.Mismatch
            : null;
}
