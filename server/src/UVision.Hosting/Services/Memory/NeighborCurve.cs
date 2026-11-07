using UVision.Api.Models;
using UVision.Api.Services.Scenarios;

namespace UVision.Api.Services.Memory;

/// <summary>이웃 순서를 남긴 재생 건 하나 — 재생 건(<see cref="ReplayCase"/>)과 그 장부 줄의 annotation.</summary>
public sealed record NeighborRecord(ReplayCase Case, IReadOnlyDictionary<string, string> Annotations);

/// <summary>
/// 이웃 수 k 곡선 — 순수 함수. 장부가 남긴 가까운 이웃의 순서(<c>memory:neighbor_*</c>)로 «k 였다면 기억은 몇 건에 답했고, 불량이 OK 로
/// 몇 장 지나갔나»를 k 마다 다시 센다. 거리 문턱은 설정된 값 그대로 두고 k 만 바꾼다 — 문턱 재생(<see cref="MemoryReplay"/>)과 같은 정의
/// (놓침 · 불량 통과율)를 그대로 쓰므로 두 표가 서로 다른 말을 하지 않는다.
/// <para>
/// 이 곡선은 <b>추천이 아니라 관찰</b>이다 — 서버는 k 를 고르지 않는다. k 가 클수록 «OK 로 답했는데 불량» 이 줄지만 기억이 답하는 비율도
/// 함께 떨어지는 두 값의 맞교환이므로(k 스윕), 어디에 둘지는 사람이 두 값을 함께 보고 정한다. 이웃 기록이 없는 줄(기록 경로가 생기기
/// 전 · 기억 부족 기권)과 기록보다 긴 k 는 다시 셀 수 없어 그 k 의 <c>counted</c> 에서 빠진다 — 센 건수를 행마다 밝힌다.
/// </para>
/// </summary>
public static class NeighborCurve
{
    /// <summary>늘 보여 줄 k — 오프라인 스윕과 같은 격자. 설정된 k 는 여기에 더해 보인다. 장부 기록 길이(<see cref="MemoryJudge.RecordedNeighbors"/>) 이하만 센다.</summary>
    public static readonly IReadOnlyList<int> Ks = [1, 3, 5, 9];

    /// <param name="records">이웃 순서를 남긴 재생 건.</param>
    /// <param name="maxDistance">거리 문턱(설정값).</param>
    /// <param name="configuredK">설정된 이웃 수 — 해당 행에 <c>configured</c> 를 단다.</param>
    /// <returns>다시 셀 수 있는 행이 하나도 없으면 null.</returns>
    public static MemoryNeighborCurve? Compute(
        IReadOnlyList<NeighborRecord> records, EffectiveScenario scenario, double maxDistance, int configuredK)
    {
        var rows = new List<MemoryNeighborRow>();
        foreach (var k in Ks.Append(configuredK).Where(k => k >= 1 && k <= MemoryJudge.RecordedNeighbors).Distinct().Order())
        {
            var cases = new List<ReplayCase>();
            foreach (var r in records)
            {
                if (MemoryJudge.Reconsider(r.Annotations, k, maxDistance) is not { } answer) continue; // 이 k 로는 다시 셀 수 없다
                cases.Add(r.Case with { AgreedClass = answer.AgreedClass, NearestDistance = answer.NearestDistance ?? r.Case.NearestDistance });
            }
            if (cases.Count == 0) continue;

            // 문턱 한 칸(설정값)만 재생한다 — 같은 정의로 답함 · 놓침 · 불량 통과율을 센다.
            var row = MemoryReplay.Run(cases, scenario, maxDistance, ladder: []).Single();
            rows.Add(new MemoryNeighborRow
            {
                K = k,
                Configured = k == configuredK,
                Counted = cases.Count,
                Answered = row.Answered,
                AnswerRate = Math.Round((double)row.Answered / cases.Count, 4),
                LabeledDefects = row.LabeledDefects,
                Missed = row.Missed,
                DefectPassRate = row.DefectPassRate,
            });
        }
        return rows.Count == 0 ? null : new MemoryNeighborCurve { MaxDistance = maxDistance, Rows = rows };
    }
}
