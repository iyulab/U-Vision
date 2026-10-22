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
    ScenarioTargets Targets,
    ScenarioTraining Training)
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
    /// 불량 유형 — 검사에서 OK 가 아닌 클래스. 선언하지 않은 검사는 <c>NG</c> 하나, 오투입 확인은 없다(불일치는
    /// 유형이 아니라 대조 결과).
    /// </summary>
    public IReadOnlyList<string> DefectClasses => DefectTypes.Select(c => c.Id).ToList();

    /// <summary><see cref="DefectClasses"/> 의 클래스 그대로 — 표시 이름이 필요한 곳(프롬프트)이 쓴다.</summary>
    public IReadOnlyList<ScenarioClass> DefectTypes => IsVerification
        ? []
        : Classes.Where(c => c.Id != ScenarioVocabulary.Ok).ToList();

    /// <summary>NG 를 유형으로 나눠 판정하는가 — 불량 유형이 <c>NG</c> 하나뿐이면 지금의 이분 판정이다.</summary>
    public bool IsMultiClass => !IsVerification && DefectClasses is not [ScenarioVocabulary.Ng];

    /// <summary>
    /// 사람 라벨로 받는 값. 검사는 클래스 id, 오투입 확인은 OK/NG(화면이 일치/불일치로 읽는다) — 판정 레코드의
    /// <c>verdict</c> 와 같은 말로 라벨을 남겨 비교 · 학습 입력이 한 체계에 있게 한다.
    /// </summary>
    public IReadOnlyList<string> Labels => IsVerification
        ? [ScenarioVocabulary.Ok, ScenarioVocabulary.Ng]
        : Classes.Select(c => c.Id).ToList();

    /// <summary>
    /// 이분 판정(<see cref="Verdict"/>)을 이 시나리오의 클래스 id 로 — 유형을 모르는 판정원(권한 단계 · 오투입 대조)의
    /// 답을 옮길 때. 오투입 확인은 «기대 품번과 같다/다르다»로 읽는다.
    /// </summary>
    public string ClassOf(Verdict verdict) => IsVerification
        ? (verdict == Verdict.OK ? ScenarioVocabulary.Match : ScenarioVocabulary.Mismatch)
        : verdict.ToString();

    /// <summary>
    /// VLM 답의 클래스. 다중분류에서 NG 는 선언한 불량 유형 하나를 대야 한다 — 못 대면 답이 아니다(null).
    /// 이분 판정 · 오투입 확인은 유형을 묻지 않으므로 <see cref="InspectionResult.DefectClass"/> 를 보지 않는다.
    /// </summary>
    public string? ClassOf(InspectionResult result)
    {
        if (!IsMultiClass || result.Verdict == Verdict.OK) return ClassOf(result.Verdict);
        return result.DefectClass is { } defect && ClassFor(defect) is { } id && id != ScenarioVocabulary.Ok ? id : null;
    }

    /// <summary>라벨(ML · 사람 · 오라클)을 선언한 클래스 id 로 — 대소문자 무시. 선언에 없으면 null.</summary>
    public string? ClassFor(string label) =>
        Classes.FirstOrDefault(c => string.Equals(c.Id, label, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// 메트릭 집계가 라벨을 NG 쪽으로 셀 규칙 — 불량 유형을 나누는 시나리오만 돌려준다(그 밖은 집계의 이분 판정 기본값).
    /// </summary>
    public Func<string, bool>? NgLabelRule => IsMultiClass ? label => VerdictOf(label) == Verdict.NG : null;

    /// <summary>클래스의 v1 보기 — 좋은 쪽(OK · MATCH)만 OK, 불량 유형은 모두 NG.</summary>
    public Verdict VerdictOf(string classId) =>
        string.Equals(classId, IsVerification ? ScenarioVocabulary.Match : ScenarioVocabulary.Ok, StringComparison.OrdinalIgnoreCase)
            ? Verdict.OK
            : Verdict.NG;

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
            scenario.Trust?.Targets ?? ScenarioTargets.Default,
            scenario.Trust?.Training ?? ScenarioTraining.Default);
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
            if (classes.FirstOrDefault(c => c.Label is { } l && !DisplayLabel.IsMatch(l)) is { } badLabel)
                return $"classes[].label 은 앞뒤 공백 · 제어문자 · ',' · '=' 없이 1~32자: {badLabel.Id}";
            if (classes.Where(c => c.Label is not null).Select(c => c.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != classes.Count(c => c.Label is not null))
                return "classes[].label 이 겹친다(대소문자 무시)";
            if (classes.Any(c => c.Settleable && c.MustCatch))
                return "한 클래스가 settleable 이면서 must_catch 일 수 없다";
            if (type == ScenarioVocabulary.Verification)
            {
                // 대조는 같다/다르다 둘뿐이고, 다른 쪽은 꼭 잡을 값이다.
                if (!(classes.Count == 2
                      && classes.Any(c => c.Id == ScenarioVocabulary.Match)
                      && classes.Any(c => c.Id == ScenarioVocabulary.Mismatch && c.MustCatch)))
                    return $"{type} 의 classes 는 MATCH · MISMATCH 둘이고 MISMATCH 는 must_catch 여야 한다";
            }
            else
            {
                // 검사는 OK 하나와 불량 유형(이분 판정이면 NG 하나). 불량 유형은 v1 에서 모두 NG 로 보이고, 꼭 잡을 값이다.
                if (!classes.Any(c => c.Id == ScenarioVocabulary.Ok && !c.MustCatch))
                    return $"{type} 의 classes 에는 must_catch 가 아닌 OK 가 있어야 한다";
                var defects = classes.Where(c => c.Id != ScenarioVocabulary.Ok).ToList();
                if (defects.Count == 0)
                    return $"{type} 의 classes 에 불량 유형이 없다 — OK 밖에 NG(또는 유형 여럿)를 둔다";
                if (defects.Any(c => !c.MustCatch))
                    return $"{type} 의 불량 유형은 모두 must_catch 여야 한다: {string.Join(", ", defects.Where(c => !c.MustCatch).Select(c => c.Id))}";
            }
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
            if (trust.Training is { } tr
                && (tr.MinPerClass <= 0 || tr.OkRatioCap <= 0 || tr.RebuildMinNewLabels <= 0))
                return "trust.training 범위: min_per_class > 0 · ok_ratio_cap > 0 · rebuild_min_new_labels > 0";
        }

        return null;
    }

    private static readonly Regex ClassId = new(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled); // \z — $ 는 끝 줄바꿈 앞에서도 맞는다

    // 표시 이름은 프롬프트에 «id(이름)» 로 들어가고 관리 화면은 «id=이름, …» 으로 적는다 — 줄을 끊거나 목록 · 쌍을
    // 흐리는 문자(제어문자 · ',' · '=')와 앞뒤 공백을 받지 않는다.
    private static readonly Regex DisplayLabel = new(@"^(?=\S)[^\p{C},=]{1,32}(?<=\S)\z", RegexOptions.Compiled);
}
