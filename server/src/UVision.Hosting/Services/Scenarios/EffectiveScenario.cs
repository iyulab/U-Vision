using System.Text.RegularExpressions;
using UVision.Api.Models;

namespace UVision.Api.Services.Scenarios;

/// <summary>
/// 유효 시나리오 — v2 필드가 모두 값을 가진 시나리오. 빈 v2 필드는 v1 동작과 같은 값으로 파생하므로,
/// v1 만 적힌 <c>scenario.json</c> 은 지금과 같은 판정을 내리는 v2 로 읽힌다. 판정 라우터의 정책 · 조각 키 ·
/// 단 순서는 여기서만 나온다.
/// </summary>
public sealed record EffectiveScenario(
    string ScenarioId,
    string Type,
    string Trigger,
    IReadOnlyList<ScenarioClass> Classes,
    IReadOnlyList<string> Tiers,
    string SliceKey,
    ScenarioTargets Targets)
{
    /// <summary>v1 검사의 클래스 — OK 는 아래 단이 확정할 수 있고, NG 는 언제나 꼭대기로 간다.</summary>
    public static readonly IReadOnlyList<ScenarioClass> InspectionClasses =
    [
        new() { Id = ScenarioVocabulary.Ok, Settleable = true },
        new() { Id = ScenarioVocabulary.Ng, MustCatch = true },
    ];

    /// <summary>오투입 확인의 클래스 — 일치는 아래 단이 확정할 수 있고, 불일치는 언제나 꼭대기로 간다.</summary>
    public static readonly IReadOnlyList<ScenarioClass> VerificationClasses =
    [
        new() { Id = ScenarioVocabulary.Match, Settleable = true },
        new() { Id = ScenarioVocabulary.Mismatch, MustCatch = true },
    ];

    public bool IsVerification => Type == ScenarioVocabulary.Verification;

    /// <summary>
    /// 이분 판정(<see cref="Verdict"/>)을 이 시나리오의 클래스 id 로. VLM · 권한 단계는 OK/NG 로 답하고, 오투입 확인은
    /// 그것을 «기대 품번과 같다/다르다»로 읽는다.
    /// </summary>
    public string ClassOf(Verdict verdict) => IsVerification
        ? (verdict == Verdict.OK ? ScenarioVocabulary.Match : ScenarioVocabulary.Mismatch)
        : verdict.ToString();

    /// <param name="scenario">저장된 시나리오(경계에서 <see cref="Validate"/> 를 통과한 것).</param>
    /// <param name="mlEnabled">전용 ML 판정원이 켜져 있는가 — 단 순서를 파생할 때만 쓴다.</param>
    public static EffectiveScenario Resolve(Scenario scenario, bool mlEnabled)
    {
        var type = scenario.Type ?? ScenarioVocabulary.Inspection;
        var verification = type == ScenarioVocabulary.Verification;
        return new(
            scenario.ScenarioId,
            type,
            scenario.Trigger ?? ScenarioVocabulary.Still,
            scenario.Classes is { Count: > 0 } classes ? classes : verification ? VerificationClasses : InspectionClasses,
            scenario.Judges is { Count: > 0 } judges ? judges.Select(j => j.Tier).ToList()
                : verification ? [ScenarioVocabulary.MatchTier, ScenarioVocabulary.VlmTier]
                : mlEnabled ? [ScenarioVocabulary.MlTier, ScenarioVocabulary.VlmTier]
                : [ScenarioVocabulary.VlmTier],
            scenario.Trust?.SliceKey ?? ScenarioVocabulary.ScenarioSlice,
            scenario.Trust?.Targets ?? ScenarioTargets.Default);
    }

    /// <summary>
    /// 경계 검증 — 지어지지 않은 값 · 모순된 구성을 거부한다(변형하지 않는다). 위반이면 사람이 읽을 사유, 아니면 null.
    /// </summary>
    public static string? Validate(ScenarioInput input)
    {
        var type = input.Type ?? ScenarioVocabulary.Inspection;
        if (input.Type is { } requested && !ScenarioVocabulary.Types.Contains(requested))
            return $"type 은 아직 {string.Join(", ", ScenarioVocabulary.Types)} 만 지원한다: {requested}";
        if (input.Trigger is { } trigger && !ScenarioVocabulary.Triggers.Contains(trigger))
            return $"trigger 는 아직 {string.Join(", ", ScenarioVocabulary.Triggers)} 만 지원한다: {trigger}";

        if (input.Classes is { } classes)
        {
            if (classes.Count == 0)
                return "classes 가 비었다 — 생략하면 OK/NG 로 파생된다";
            if (classes.Any(c => !ClassId.IsMatch(c.Id ?? "")))
                return "classes[].id 는 영숫자 · '_' · '-' 1~64자";
            if (classes.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != classes.Count)
                return "classes[].id 가 겹친다(대소문자 무시)";
            if (classes.Any(c => c.Settleable && c.MustCatch))
                return "한 클래스가 settleable 이면서 must_catch 일 수 없다";
            // 두 유형 모두 이분 판정(OK/NG)에서 클래스로 옮기므로 클래스가 둘이고, 나쁜 쪽은 꼭 잡을 값이다.
            var (good, bad) = type == ScenarioVocabulary.Verification
                ? (ScenarioVocabulary.Match, ScenarioVocabulary.Mismatch)
                : (ScenarioVocabulary.Ok, ScenarioVocabulary.Ng);
            if (!(classes.Count == 2
                  && classes.Any(c => c.Id == good)
                  && classes.Any(c => c.Id == bad && c.MustCatch)))
                return $"{type} 의 classes 는 {good} · {bad} 둘이고 {bad} 는 must_catch 여야 한다";
        }

        if (input.Judges is { } judges)
        {
            if (judges.Count == 0)
                return "judges 가 비었다 — 생략하면 ML 활성 여부로 파생된다";
            var allowed = ScenarioVocabulary.TiersFor(type);
            if (judges.Any(j => !allowed.Contains(j.Tier ?? "")))
                return $"{type} 의 judges[].tier 는 {string.Join(", ", allowed)} 중 하나";
            if (judges.Select(j => j.Tier).Distinct(StringComparer.Ordinal).Count() != judges.Count)
                return "judges[].tier 가 겹친다";
            // 꼭대기는 VLM — 아래 단이 모두 기권해도 답할 단이 있어야 하고, 지금 교사는 VLM 이다.
            if (judges[^1].Tier != ScenarioVocabulary.VlmTier)
                return "judges 의 마지막(꼭대기)은 vlm 이어야 한다";
        }

        if (input.Trust is { } trust)
        {
            if (trust.SliceKey is { } slice && !ScenarioVocabulary.SliceKeys.Contains(slice))
                return $"trust.slice_key 는 아직 {string.Join(", ", ScenarioVocabulary.SliceKeys)} 만 지원한다: {slice}";
            if (trust.Targets is { } t
                && (t.Agreement is <= 0 or > 1 || t.MaxMissRate is < 0 or >= 1 || t.SampleSize <= 0))
                return "trust.targets 범위: 0 < agreement ≤ 1 · 0 ≤ max_miss_rate < 1 · sample_size > 0";
        }

        return null;
    }

    private static readonly Regex ClassId = new(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled); // \z — $ 는 끝 줄바꿈 앞에서도 맞는다
}
