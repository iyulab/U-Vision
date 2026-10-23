using System.Globalization;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;

namespace UVision.Api.Services.Memory;

/// <summary>재생할 판정 한 건 — 장부의 기억 annotation 과 실제로 쓴 판정, 있으면 사람 라벨.</summary>
/// <param name="AgreedClass">이웃이 모인 클래스(갈렸으면 null).</param>
/// <param name="NearestDistance">최근접 확정 사례까지의 코사인 거리.</param>
/// <param name="TeacherClass">실제로 쓴 판정의 클래스(지금은 VLM — 판정 불가면 null).</param>
/// <param name="HumanLabel">사람이 확정한 라벨(없으면 null).</param>
public sealed record ReplayCase(string? AgreedClass, double NearestDistance, string? TeacherClass, string? HumanLabel);

/// <summary>
/// 기억 단 문턱 재생 — 순수 함수. «문턱이 t 였다면 기억은 몇 건에 답했고, 그 답이 교사(실제 판정) · 사람 라벨과 얼마나
/// 맞았나»를 문턱마다 센다. 문턱을 사람이 감으로 쓰지 않고 이 표로 고른다(설계 §4).
/// <para>
/// <b>놓침</b>은 기억이 확정 가능한 클래스(OK)로 답했는데 사람 라벨이 놓치면 안 되는 클래스(불량)인 건이다 — 기억이 Active 가
/// 되면 그대로 통과했을 위험한 오답이다. 비율의 분모가 0 이면 비율은 null(«데이터 없음»을 0 으로 꾸미지 않는다).
/// </para>
/// </summary>
public static class MemoryReplay
{
    /// <summary>설정 문턱과 함께 늘 보여 줄 문턱 사다리.</summary>
    public static readonly IReadOnlyList<double> Ladder = [0.05, 0.10, 0.15, 0.20, 0.30];

    public static IReadOnlyList<MemoryReplayRow> Run(
        IReadOnlyList<ReplayCase> cases, EffectiveScenario scenario, double configured)
    {
        var thresholds = Ladder.Append(configured).Distinct().Order().ToList();
        return [.. thresholds.Select(t => RowAt(t, cases, scenario, configured))];
    }

    private static MemoryReplayRow RowAt(double t, IReadOnlyList<ReplayCase> cases, EffectiveScenario scenario, double configured)
    {
        var answered = cases.Where(c => c.AgreedClass is not null && c.NearestDistance <= t).ToList();
        var taught = answered.Where(c => c.TeacherClass is not null).ToList();
        var labeled = answered.Where(c => c.HumanLabel is not null && scenario.ClassFor(c.HumanLabel) is not null).ToList();
        var missed = labeled.Count(c => IsSettleable(scenario, c.AgreedClass!) && IsMustCatch(scenario, c.HumanLabel!));

        return new MemoryReplayRow
        {
            MaxDistance = t,
            Configured = t == configured,
            Answered = answered.Count,
            TeacherChecked = taught.Count,
            TeacherAgreement = Rate(taught.Count(c => Same(c.AgreedClass!, c.TeacherClass!)), taught.Count),
            Labeled = labeled.Count,
            LabelAgreement = Rate(labeled.Count(c => Same(c.AgreedClass!, c.HumanLabel!)), labeled.Count),
            Missed = missed,
            MissRate = Rate(missed, labeled.Count),
        };
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsSettleable(EffectiveScenario s, string cls) =>
        s.Classes.Any(c => c.Settleable && Same(c.Id, cls));

    private static bool IsMustCatch(EffectiveScenario s, string cls) =>
        s.Classes.Any(c => c.MustCatch && Same(c.Id, cls));

    private static double? Rate(int n, int d) => d == 0 ? null : Math.Round((double)n / d, 4);

    /// <summary>
    /// 장부 annotation 에서 재생 건을 만든다 — 기억이 거리를 남기지 않은 판정(기억 없음 · 임베딩 실패 · 이웃 부족)은 재생할
    /// 것이 없어 null. 교사 클래스는 실제로 쓴 판정(<c>operative:class</c>, 없으면 <c>operative:verdict</c> 의 클래스).
    /// </summary>
    public static ReplayCase? FromAnnotations(
        IReadOnlyDictionary<string, string>? annotations, EffectiveScenario scenario, string? humanLabel)
    {
        if (annotations is null
            || !annotations.TryGetValue("memory:nearest_distance", out var d)
            || !double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance))
            return null;
        annotations.TryGetValue("memory:agreed_class", out var agreed);
        string? teacher = annotations.TryGetValue("operative:class", out var cls) ? cls
            : annotations.TryGetValue("operative:verdict", out var v) && Enum.TryParse<Verdict>(v, out var verdict) ? scenario.ClassOf(verdict)
            : null;
        return new ReplayCase(agreed, distance, teacher, humanLabel);
    }
}
