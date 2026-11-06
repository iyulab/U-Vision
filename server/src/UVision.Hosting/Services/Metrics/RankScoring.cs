namespace UVision.Api.Services.Metrics;

/// <summary>
/// 점수의 순위만 보는 지표 — 문턱 · 작동점에 기대지 않는다. 같은 모델이라도 한 작동점(argmax)의 재현율은 문턱 하나로 크게 갈리지만
/// 순위 품질은 안정적이므로, 격상 신호 · 측정 도구가 이 지표를 같은 정의로 읽는다.
/// </summary>
public static class RankScoring
{
    /// <summary>리포트가 함께 읽는 허용 정상 오경보율 — 패치 · 장부 재생 · ML 분류가 같은 눈금으로 견준다.</summary>
    public static readonly double[] StandardFalseAlarmRates = [0.01, 0.05, 0.10];

    /// <summary>불량 점수가 정상 점수보다 높을 확률(동점은 절반) — 순위만 보므로 점수의 단위와 무관하다. 한쪽이 비면 NaN.</summary>
    public static double Auroc(IReadOnlyList<double> normal, IReadOnlyList<double> defect)
    {
        if (normal.Count == 0 || defect.Count == 0) return double.NaN;
        var wins = 0.0;
        foreach (var d in defect)
            foreach (var n in normal)
                wins += d > n ? 1 : d == n ? 0.5 : 0;
        return wins / ((double)normal.Count * defect.Count);
    }

    /// <summary>정상 오경보가 <paramref name="falseAlarmRate"/> 이하가 되는 가장 낮은 문턱에서, 그 문턱 이하(= OK 로 통과)인 불량 수. 동점은 통과로 센다.</summary>
    public static int MissedAtFalseAlarm(IReadOnlyList<double> normal, IReadOnlyList<double> defect, double falseAlarmRate)
    {
        if (normal.Count == 0) return defect.Count;
        var sorted = normal.Order().ToList();
        // 문턱 t 보다 큰 정상이 오경보다 — 허용 오경보 수만큼 위쪽을 남기고 그 아래 최댓값을 문턱으로.
        var allowed = (int)Math.Floor(falseAlarmRate * sorted.Count);
        var threshold = sorted[sorted.Count - 1 - allowed];
        return defect.Count(d => d <= threshold);
    }

    /// <summary>오경보를 <paramref name="falseAlarmRate"/> 이하로 묶었을 때 잡은 불량의 비율. 정상이나 불량이 없으면 null.</summary>
    public static double? RecallAtFalseAlarm(IReadOnlyList<double> normal, IReadOnlyList<double> defect, double falseAlarmRate) =>
        normal.Count == 0 || defect.Count == 0
            ? null
            : 1.0 - (double)MissedAtFalseAlarm(normal, defect, falseAlarmRate) / defect.Count;
}
