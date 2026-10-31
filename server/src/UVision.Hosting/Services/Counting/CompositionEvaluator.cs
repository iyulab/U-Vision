namespace UVision.Api.Services.Counting;

/// <summary>구성 항목 하나의 상태 — 기대와 센 수를 견준 결과.</summary>
public static class ItemStatus
{
    public const string Ok = "ok";

    /// <summary>센 수가 기대보다 적다.</summary>
    public const string Missing = "missing";

    /// <summary>센 수가 기대보다 많거나 기대에 없는 종류가 검출됐다(기대 0).</summary>
    public const string Extra = "extra";
}

/// <summary>기대에 없거나 기대보다 많이 검출된 항목을 어떻게 볼 것인가.</summary>
public static class ExtraPolicies
{
    /// <summary>과잉도 불량(기본) — 느슨하게 두면 NG 를 놓친다.</summary>
    public const string Ng = "ng";

    /// <summary>과잉은 항목에 올리되 판정은 막지 않는다.</summary>
    public const string Warn = "warn";

    /// <summary>과잉은 보지 않는다(기대 이상이면 맞는 것으로, 기대에 없는 종류는 항목에 올리지 않는다).</summary>
    public const string Ignore = "ignore";

    public static readonly IReadOnlyList<string> All = [Ng, Warn, Ignore];
}

/// <summary>종류별 기대 대비 센 결과 한 줄.</summary>
public sealed record CountItem(string Class, int Expected, int Found, string Status);

/// <summary>
/// 구성 확인(종류마다 기대 개수가 있는 계수) — 순수 함수다. 센 결과를 종류별 기대와 견줘 항목 목록(있음 · 누락 · 과잉)과 판정을 낸다.
/// 세는 일(신뢰도 문턱 · 종류 거름 · 근거 상자)은 <see cref="CountEvaluator"/> 와 같은 규칙을 그대로 쓴다.
/// </summary>
public static class CompositionEvaluator
{
    /// <param name="detections">검출기가 낸 상자 전부(신뢰도 문턱 전).</param>
    /// <param name="minConfidence">이 신뢰도 이상인 상자만 센다.</param>
    /// <param name="classes">세는 물건 종류. null · 빈 목록이면 검출된 모든 종류를 센다(기대에 없는 종류가 과잉으로 잡히려면 이쪽이어야 한다).</param>
    /// <param name="expectedByClass">종류 → 기대 개수.</param>
    /// <param name="extraPolicy"><see cref="ExtraPolicies"/> 중 하나.</param>
    /// <param name="uncertainBand">«문턱 근처» 의 폭.</param>
    /// <returns>
    /// <see cref="CountResult.Items"/> 가 채워진 결과. <see cref="CountResult.Expected"/> 는 기대 합계, <see cref="CountResult.Tolerance"/> 는
    /// 0(구성에는 허용 오차가 없다 — 누락은 언제나 불량), <see cref="CountResult.WithinRange"/> 는 «구성이 맞다» 이다.
    /// </returns>
    public static CountResult Evaluate(
        IReadOnlyList<Detection> detections, double minConfidence, IReadOnlyList<string>? classes,
        IReadOnlyDictionary<string, int> expectedByClass, string extraPolicy = ExtraPolicies.Ng,
        double uncertainBand = CountEvaluator.DefaultUncertainBand)
    {
        var counted = CountEvaluator.Count(detections, minConfidence, classes, expected: null, tolerance: null, uncertainBand);

        var kinds = expectedByClass.Keys.Order(StringComparer.Ordinal)
            .Concat(counted.ByClass.Keys.Where(k => !expectedByClass.ContainsKey(k)).Order(StringComparer.Ordinal))
            .ToList();

        var items = new List<CountItem>();
        foreach (var kind in kinds)
        {
            var expected = expectedByClass.GetValueOrDefault(kind);
            var found = counted.ByClass.GetValueOrDefault(kind);
            var status = found < expected ? ItemStatus.Missing
                : found > expected ? (extraPolicy == ExtraPolicies.Ignore ? ItemStatus.Ok : ItemStatus.Extra)
                : ItemStatus.Ok;
            // 무시하기로 한 «기대에 없는 종류» 는 항목에 올리지 않는다(기대 이상은 맞는 것으로 보되 센 수는 그대로 적는다).
            if (extraPolicy == ExtraPolicies.Ignore && expected == 0 && found > 0)
                continue;
            items.Add(new CountItem(kind, expected, found, status));
        }

        // 문턱 근처 상자를 종류마다 넣고 빼서 닿을 수 있는 개수에서 결론이 하나가 아니면 흔들리는 사진이다. 종류마다 독립이라
        // «맞을 수 있는가» 는 모든 종류가 각자 맞는 개수를 가질 수 있는가, «틀릴 수 있는가» 는 한 종류라도 틀린 개수를 가질 수 있는가다.
        var filtered = classes is { Count: > 0 }
            ? detections.Where(d => classes.Contains(d.Class, StringComparer.Ordinal)).ToList()
            : detections;
        var canBeRight = true;
        var canBeWrong = false;
        foreach (var kind in expectedByClass.Keys.Concat(filtered.Select(d => d.Class)).Distinct(StringComparer.Ordinal))
        {
            var expected = expectedByClass.GetValueOrDefault(kind);
            var strict = filtered.Count(d => d.Class == kind && d.Confidence >= minConfidence + uncertainBand);
            var loose = filtered.Count(d => d.Class == kind && d.Confidence >= minConfidence - uncertainBand);
            var reachable = Enumerable.Range(strict, loose - strict + 1).Select(n => Right(n, expected, extraPolicy)).ToList();
            canBeRight &= reachable.Contains(true);
            canBeWrong |= reachable.Contains(false);
        }

        var right = kinds.All(k => Right(counted.ByClass.GetValueOrDefault(k), expectedByClass.GetValueOrDefault(k), extraPolicy));

        return counted with
        {
            Items = items,
            Expected = expectedByClass.Values.Sum(),
            Tolerance = 0,
            WithinRange = right,
            Uncertain = canBeRight && canBeWrong,
        };
    }

    // 기대 미만은 언제나 틀리다. 기대 초과는 과잉을 불량으로 보는 정책일 때만 틀리다.
    private static bool Right(int found, int expected, string extraPolicy) =>
        found >= expected && (extraPolicy != ExtraPolicies.Ng || found == expected);
}
