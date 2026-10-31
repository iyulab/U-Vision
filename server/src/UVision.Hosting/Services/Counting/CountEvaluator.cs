namespace UVision.Api.Services.Counting;

/// <summary>검출기가 낸 상자 하나 — 좌표는 원본 이미지 기준 비율(0~1)이라 다운스케일에 의존하지 않는다.</summary>
public sealed record Detection(string Class, double X, double Y, double W, double H, double Confidence);

/// <summary>개수 세기의 결과 — 세는 일과 허용 범위 대조만 담는 순수 값이다(검출기 호출과 섞지 않는다).</summary>
public sealed record CountResult
{
    /// <summary>문턱을 넘고 세는 종류에 드는 상자 수.</summary>
    public required int Total { get; init; }

    public required IReadOnlyDictionary<string, int> ByClass { get; init; }

    /// <summary>센 상자(근거로 보인다).</summary>
    public required IReadOnlyList<Detection> Boxes { get; init; }

    /// <summary>
    /// 세지 않았지만 문턱 바로 아래(띠 안)라 «넣었다면 개수가 달라졌을» 상자 — 흔들림의 원인을 사람이 사진에서 찾게 한다.
    /// 세는 종류가 아닌 상자는 들지 않는다.
    /// </summary>
    public IReadOnlyList<Detection> NearBoxes { get; init; } = [];

    public int? Expected { get; init; }

    public int? Tolerance { get; init; }

    /// <summary>종류별 기대 대비 항목 목록 — 구성 확인(<see cref="CompositionEvaluator"/>)일 때만. 총 개수만 견주는 계수는 null.</summary>
    public IReadOnlyList<CountItem>? Items { get; init; }

    /// <summary>
    /// 기대가 있을 때만 — 센 수가 <c>expected ± tolerance</c> 안인가(구성 확인이면 모든 항목이 맞는가). 기대가 없으면 null(세기만).
    /// </summary>
    public bool? WithinRange { get; init; }

    /// <summary>
    /// 문턱 근처 상자 때문에 결론이 흔들린다 — 기대가 있으면 <c>WithinRange</c> 가, 없으면 개수가 신뢰도 문턱 ± 띠 안의 상자를 넣고 빼는 데 따라 달라진다.
    /// 흔들리는 사진은 사람 확인 쪽(안전한 쪽)으로 둔다.
    /// </summary>
    public required bool Uncertain { get; init; }
}

/// <summary>
/// 수 세기(신뢰도 문턱 → 클래스별 개수)와 허용 범위 대조 — 순수 함수다. 검출기 단이 상자를 내면 여기서 개수를 정한다.
/// </summary>
public static class CountEvaluator
{
    /// <summary>문턱 ± 이 값 안의 상자는 «있을 수도 없을 수도» 로 본다.</summary>
    public const double DefaultUncertainBand = 0.1;

    /// <param name="detections">검출기가 낸 상자 전부(신뢰도 문턱 전).</param>
    /// <param name="minConfidence">이 신뢰도 이상인 상자만 센다.</param>
    /// <param name="classes">세는 물건 종류. null · 빈 목록이면 검출된 모든 종류를 센다.</param>
    /// <param name="expected">기대 개수. null 이면 세기만 한다.</param>
    /// <param name="tolerance">허용 오차(기본 0 = 정확히). 음수는 0 으로 본다.</param>
    /// <param name="uncertainBand">«문턱 근처» 의 폭.</param>
    public static CountResult Count(
        IReadOnlyList<Detection> detections, double minConfidence, IReadOnlyList<string>? classes,
        int? expected, int? tolerance, double uncertainBand = DefaultUncertainBand)
    {
        var tol = expected is null ? (int?)null : Math.Max(0, tolerance ?? 0);
        var counted = Filter(detections, classes);
        var boxes = counted.Where(d => d.Confidence >= minConfidence).ToList();

        // 문턱 근처 상자를 넣고 빼서 닿을 수 있는 개수(모두 빼면 strict, 모두 넣으면 loose, 그 사이는 일부만)에서 결론이 하나가
        // 아니면 흔들리는 사진이다. 양 끝만 견주면 «끝은 둘 다 밖인데 가운데(지금 센 수)만 안» 인 사진을 놓친다.
        var strict = counted.Count(d => d.Confidence >= minConfidence + uncertainBand);
        var loose = counted.Count(d => d.Confidence >= minConfidence - uncertainBand);
        var uncertain = expected is { } e
            ? Enumerable.Range(strict, loose - strict + 1).Select(n => Within(n, e, tol!.Value)).Distinct().Count() > 1
            : strict != loose;

        return new CountResult
        {
            Total = boxes.Count,
            ByClass = boxes.GroupBy(d => d.Class, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Boxes = boxes,
            NearBoxes = [.. counted.Where(d => d.Confidence < minConfidence && d.Confidence >= minConfidence - uncertainBand)],
            Expected = expected,
            Tolerance = tol,
            WithinRange = expected is { } exp ? Within(boxes.Count, exp, tol!.Value) : null,
            Uncertain = uncertain,
        };
    }

    private static IEnumerable<Detection> Filter(IReadOnlyList<Detection> detections, IReadOnlyList<string>? classes) =>
        classes is { Count: > 0 }
            ? detections.Where(d => classes.Contains(d.Class, StringComparer.Ordinal))
            : detections;

    private static bool Within(int count, int expected, int tolerance) => Math.Abs(count - expected) <= tolerance;
}
