using UVision.Api.Configuration;
using UVision.Api.Models;

namespace UVision.Api.Services.Authority;

/// <summary>
/// 권한 단계의 안전쪽 자동격하 판정(A1, 순수). ML 이 조용히 열화하면 한 단계 내린다 — 격상은 사람,
/// 격하는 자동(NG-safety 비대칭). 발동 오케스트레이션(주기·atomic 쓰기·degrade-safe)은 inspect 가 소유.
/// </summary>
public static class AutoDemoteEvaluator
{
    /// <returns>격하 목표 단계(현재의 1단계 아래) 또는 null(유지).</returns>
    public static AuthorityStage? Evaluate(
        AuthorityStage stage, MetricsSummary summary, AuthorityOptions options)
    {
        // ML 이 운영 판정에 영향을 주는 단계만 대상(advisory/shadow 는 이미 VLM 기준선).
        if (stage != AuthorityStage.CoPrimary && stage != AuthorityStage.MlPrimary)
            return null;
        if (summary.Inspections < options.MinWindow)
            return null;
        if (summary.MlNgRecall is not { } ml || summary.VlmNgRecall is not { } vlm)
            return null; // 측정 불가 → 함부로 내리지 않음(정직)

        var unsafe_ = ml < vlm || ml < options.RecallFloor;
        return unsafe_ ? (AuthorityStage)((int)stage - 1) : null;
    }
}
