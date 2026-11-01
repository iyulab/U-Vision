using System.Globalization;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;

namespace UVision.Api.Services.Memory;

/// <summary>곡선에 들 판정 한 건 — 사람 라벨이 있는 것만. 기억 칸은 기억이 꺼졌던 시절의 줄이면 비어 있다.</summary>
/// <param name="OperativeClass">실제로 쓴 판정의 클래스(판정 불가면 null).</param>
/// <param name="MemoryAgreedClass">기억 이웃이 모인 클래스(갈렸거나 기억이 없으면 null).</param>
/// <param name="MemoryNearestDistance">최근접 확정 사례까지의 코사인 거리(기억이 거리를 남기지 않았으면 null).</param>
/// <param name="HumanLabel">사람이 확정한 라벨 — 정답.</param>
/// <param name="MlLabel">전용 ML 이 낸 라벨(ML 을 묻지 않았거나 분류가 실패했으면 null).</param>
public sealed record CurveCase(
    string? OperativeClass, string? MemoryAgreedClass, double? MemoryNearestDistance, string HumanLabel, string? MlLabel = null);

/// <summary>사람 확정 라벨이 <see cref="LabeledThrough"/> 건 쌓일 때까지의 한 구간.</summary>
/// <param name="LabeledThrough">이 구간 끝까지 쌓인 라벨 있는 판정 수(누적).</param>
/// <param name="Judged">이 구간의 라벨 있는 판정 수.</param>
/// <param name="OperativeAgreement">실제 판정이 사람 라벨과 같았던 비율(판정 클래스가 있던 건 가운데).</param>
/// <param name="LabeledDefects">사람 라벨이 불량(놓치면 안 되는 클래스)인 수.</param>
/// <param name="OperativeMissed">그 불량을 실제 판정이 OK(확정 가능한 클래스)로 지나가게 한 수.</param>
/// <param name="OperativeDefectPass">불량 통과율 = 놓침 / 라벨 있는 불량(불량이 없으면 null).</param>
/// <param name="MemoryAnswered">문턱 안에서 기억이 답했을 판정 수.</param>
/// <param name="MemoryAnsweredRate">기억이 답한 비율 = 답함 / 이 구간 판정 수.</param>
/// <param name="MemoryAgreement">기억 답이 사람 라벨과 같았던 비율(답한 건이 없으면 null).</param>
/// <param name="MemoryMissed">기억이 OK 로 답했는데 사람 라벨이 불량인 수 — 기억이 확정했다면 통과시켰을 오답.</param>
/// <param name="MlChecked">전용 ML 이 라벨을 낸 판정 수(ML 을 켜지 않았거나 분류가 실패한 건은 빠진다).</param>
/// <param name="MlAgreement">ML 라벨이 사람 라벨과 같았던 비율(ML 이 답한 건이 없으면 null).</param>
/// <param name="MlMissed">ML 이 OK 로 답했는데 사람 라벨이 불량인 수.</param>
public sealed record CurvePoint(
    int LabeledThrough, int Judged,
    double? OperativeAgreement, int LabeledDefects, int OperativeMissed, double? OperativeDefectPass,
    int MemoryAnswered, double MemoryAnsweredRate, double? MemoryAgreement, int MemoryMissed,
    int MlChecked = 0, double? MlAgreement = null, int MlMissed = 0);

/// <summary>
/// 플라이휠 곡선 — 순수 함수. 사람이 확정한 라벨이 쌓이는 순서대로 «실제 판정» 과 «기억 단» 의 정확도를 구간마다 센다. 쓸수록
/// 나아진다는 주장의 증거이자 성공 기준이다(곡선이 없으면 주장하지 않는다). 정답은 사람 확정 라벨뿐이고 판정원 자신의 답은 정답이
/// 아니다. 실제 판정 · 기억 단은 장부 annotation 에서, 전용 ML 은 판정마다 한 줄 남는 메트릭 행의 라벨에서 읽는다.
/// </summary>
public static class FlywheelCurve
{
    /// <param name="cases">시간순 — 장부를 날짜 순으로 읽은 순서.</param>
    /// <param name="bucketSize">구간 하나의 라벨 있는 판정 수(1 이상). 마지막 구간은 모자랄 수 있다.</param>
    /// <param name="memoryMaxDistance">기억이 답했다고 볼 거리 상한(그 시나리오의 설정 문턱).</param>
    public static IReadOnlyList<CurvePoint> Build(
        IReadOnlyList<CurveCase> cases, EffectiveScenario scenario, int bucketSize, double memoryMaxDistance)
    {
        if (bucketSize < 1)
            throw new ArgumentOutOfRangeException(nameof(bucketSize), "구간 크기는 1 이상이어야 합니다");

        var points = new List<CurvePoint>();
        var through = 0;
        foreach (var bucket in cases.Chunk(bucketSize))
        {
            through += bucket.Length;
            points.Add(PointOf(bucket, through, scenario, memoryMaxDistance));
        }
        return points;
    }

    private static CurvePoint PointOf(CurveCase[] bucket, int through, EffectiveScenario scenario, double memoryMaxDistance)
    {
        var decided = bucket.Where(c => c.OperativeClass is not null).ToList();
        var defects = bucket.Where(c => IsMustCatch(scenario, c.HumanLabel)).ToList();
        var passed = defects.Count(c => c.OperativeClass is not null && IsSettleable(scenario, c.OperativeClass));
        var answered = bucket
            .Where(c => c.MemoryAgreedClass is not null && c.MemoryNearestDistance is { } d && d <= memoryMaxDistance)
            .ToList();
        var memoryMissed = answered.Count(c => IsSettleable(scenario, c.MemoryAgreedClass!) && IsMustCatch(scenario, c.HumanLabel));
        var ml = bucket.Where(c => c.MlLabel is not null).ToList();
        var mlMissed = ml.Count(c => IsSettleable(scenario, c.MlLabel!) && IsMustCatch(scenario, c.HumanLabel));

        return new CurvePoint(
            through, bucket.Length,
            Rate(decided.Count(c => Same(c.OperativeClass!, c.HumanLabel)), decided.Count),
            defects.Count, passed, Rate(passed, defects.Count),
            answered.Count, Math.Round((double)answered.Count / bucket.Length, 4),
            Rate(answered.Count(c => Same(c.MemoryAgreedClass!, c.HumanLabel)), answered.Count), memoryMissed,
            ml.Count, Rate(ml.Count(c => Same(c.MlLabel!, c.HumanLabel)), ml.Count), mlMissed);
    }

    /// <summary>
    /// 장부 annotation 과 사람 라벨에서 곡선 한 건을 만든다 — 사람 라벨이 없으면 정답을 몰라 null. 기억 annotation 이 없는
    /// 줄(기억이 꺼졌던 시절)도 «실제 판정» 곡선에는 든다. 실제 판정 클래스는 <see cref="MemoryReplay"/> 와 같은 규칙으로 읽는다.
    /// </summary>
    /// <param name="mlLabel">그 판정의 메트릭 행이 가진 전용 ML 라벨(없으면 null) — 장부 annotation 에는 ML 의 답이 없다.</param>
    public static CurveCase? FromAnnotations(
        IReadOnlyDictionary<string, string>? annotations, EffectiveScenario scenario, string? humanLabel, string? mlLabel = null)
    {
        if (humanLabel is null || annotations is null)
            return null;
        string? operative = annotations.TryGetValue("operative:class", out var cls) ? cls
            : annotations.TryGetValue("operative:verdict", out var v) && Enum.TryParse<Verdict>(v, out var verdict) ? scenario.ClassOf(verdict)
            : null;
        annotations.TryGetValue("memory:agreed_class", out var agreed);
        double? distance = annotations.TryGetValue("memory:nearest_distance", out var d)
            && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        return new CurveCase(operative, agreed, distance, humanLabel, mlLabel);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsSettleable(EffectiveScenario s, string cls) => s.Classes.Any(c => c.Settleable && Same(c.Id, cls));

    private static bool IsMustCatch(EffectiveScenario s, string cls) => s.Classes.Any(c => c.MustCatch && Same(c.Id, cls));

    private static double? Rate(int n, int d) => d == 0 ? null : Math.Round((double)n / d, 4);
}
