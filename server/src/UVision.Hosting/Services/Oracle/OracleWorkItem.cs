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
}
