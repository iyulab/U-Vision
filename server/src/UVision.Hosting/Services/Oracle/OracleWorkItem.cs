using UVision.Api.Models;
using UVision.Api.Services.Label;

namespace UVision.Api.Services.Oracle;

/// <summary>스윕 대상 1건 — 시나리오·날짜·이미지.</summary>
public sealed record OracleWorkItem
{
    public required string ScenarioId { get; init; }
    public required string Date { get; init; }
    public required string ImageId { get; init; }
}

/// <summary>오라클 스윕의 순수 판단 로직 — I/O 없음(결정론·테스트 용이).</summary>
public static class OracleSweepPlanner
{
    /// <summary>requires_review 이고 아직 오라클 소견이 없으면 처리 대상(멱등성 기준).</summary>
    public static bool NeedsOracle(StoredResult result, StoredLabel? sidecar)
    {
        if (result.RequiresReview != true)
            return false;
        var oracled = sidecar?.History?.Any(e => e.Mode == LabelMode.Oracle) ?? false;
        return !oracled;
    }

    /// <summary>egress 게이트(E4) — 로컬(IsCloud=false)은 항상 허용, cloud 는 per-scenario opt-in.</summary>
    public static bool EgressAllowed(bool providerIsCloud, bool scenarioAllowsEgress) =>
        !providerIsCloud || scenarioAllowsEgress;

    /// <summary>
    /// 틱별 시작점 회전 — BatchCap order-bias 완화(④-B 후속). 고정 순서 스윕은 앞쪽 고볼륨
    /// 시나리오가 budget 을 매 틱 선점해 뒤쪽 시나리오 백로그 처리가 지연된다. offset(스윕 횟수)으로
    /// 시작 시나리오를 round-robin 회전시켜 공정성을 best-effort 로 회복한다(부호·범위 안전).
    /// 시나리오 내 날짜는 recent-first 우선순위 유지(의도적).
    /// </summary>
    public static IReadOnlyList<T> Rotated<T>(IReadOnlyList<T> items, int offset)
    {
        int n = items.Count;
        if (n <= 1)
            return items;
        int start = ((offset % n) + n) % n; // 음수 offset 안전
        if (start == 0)
            return items;
        var rotated = new List<T>(n);
        for (int i = 0; i < n; i++)
            rotated.Add(items[(start + i) % n]);
        return rotated;
    }
}
