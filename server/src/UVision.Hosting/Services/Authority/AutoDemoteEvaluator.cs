using UVision.Api.Configuration;
using UVision.Api.Models;

namespace UVision.Api.Services.Authority;

/// <summary>
/// 권한 단계의 안전쪽 자동격하 판정(A1, 순수). ML 이 조용히 열화하면 한 단계 내린다 — 격상은 사람,
/// 격하는 자동(NG-safety 비대칭). 발동 오케스트레이션(주기·atomic 쓰기·degrade-safe)은 inspect 가 소유.
/// </summary>
public static class AutoDemoteEvaluator
{
    /// <summary>
    /// 자동격하가 읽을 메트릭 행 — 같은 날짜 버킷 안에서 ML 모델 판이 바뀌었으면 «가장 최근에 답한 판» 의 행만 남긴다.
    /// 판이 섞인 버킷의 재현율은 좋은 옛 판의 적중이 나빠진 새 판의 놓침을 가려(예: 옛 판 1200건 적중 · 새 판 60건 전부 놓침 = 0.952)
    /// 격하해야 할 때 격하하지 않는다 — 안전쪽 비대칭이 깨지는 방향이다. 격상 신호의 기준 판(D-119)과 같은 규칙이다.
    /// 판 신원을 하나도 싣지 않은 버킷(옛 행 · 신원을 보고하지 않는 서버 · mock)은 그대로 돌려준다 — 이전과 같은 판정.
    /// 신원 없는 degrade 행은 새 판이 답했는지 알 수 없어 함께 빠진다(재현율 분모에 원래 들지 않는다).
    /// </summary>
    public static IReadOnlyList<MetricsRow> CurrentModelRows(IReadOnlyList<MetricsRow> rows)
    {
        var current = rows.LastOrDefault(r => r.MlModel is not null)?.MlModel;
        if (current is null)
            return rows;
        // ML 을 묻지 않은 행(vlm_only)은 모델 판이 없는 정상 행이라 남긴다 — VLM 쪽 항을 이전과 같게 둔다.
        return rows
            .Where(r => r.VlmOnly || string.Equals(r.MlModel, current, StringComparison.Ordinal))
            .ToList();
    }

    /// <returns>격하 목표 단계(현재의 1단계 아래) 또는 null(유지).</returns>
    public static AuthorityStage? Evaluate(
        AuthorityStage stage, MetricsSummary summary, AuthorityOptions options)
    {
        // ML 이 운영 판정에 영향을 주는 단계만 대상(advisory/shadow 는 이미 VLM 기준선).
        if (stage != AuthorityStage.CoPrimary && stage != AuthorityStage.MlPrimary)
            return null;
        if (summary.MlChecked < options.MinWindow) // ML 을 물은 건만 — ① 단계 행(vlm_only)은 ML 신뢰의 표본이 아니다
            return null;
        if (summary.MlNgRecall is not { } ml || summary.VlmNgRecall is not { } vlm)
            return null; // 측정 불가 → 함부로 내리지 않음(정직)

        var unsafe_ = ml < vlm || ml < options.RecallFloor;
        return unsafe_ ? (AuthorityStage)((int)stage - 1) : null;
    }
}
