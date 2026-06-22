using UVision.Api.Models;
using UVision.Api.Services.Confidence;
using UVision.Api.Services.Ml;

namespace UVision.Api.Services.DualCheck;

/// <summary>
/// 2중체크 결과(신뢰성 플라이휠 ③) — VLM 판정과 전용 ML 분류의 교차검증 산출물.
/// VLM verdict 는 여전히 권위 있는 판정이고(현 구현 정직성), ML 은 <b>교차검증</b>이다.
/// 불일치/저신뢰는 <see cref="RequiresReview"/> 로 표면화 → ④ 오라클/HITL 에스컬레이션의 입력.
/// </summary>
public sealed record DualCheckResult
{
    /// <summary>ML 분류 라벨(class-agnostic 문자열, 예: "ok"/"ng").</summary>
    public required string MlLabel { get; init; }

    /// <summary>ML 예측 클래스 신뢰도(0.0~1.0).</summary>
    public required double MlConfidence { get; init; }

    /// <summary>VLM verdict 와 ML 라벨이 같은 클래스로 일치하는가.</summary>
    public required bool Agreement { get; init; }

    /// <summary>불일치 또는 저신뢰 → 사람/오라클 검토 필요(④). 일치+충분신뢰면 false(자동확정).</summary>
    public required bool RequiresReview { get; init; }
}

/// <summary>
/// VLM 판정 + ML 분류를 비교해 2중체크 결과를 산출하는 순수 함수(결정론·테스트 용이).
/// 병렬 호출(<c>Task.WhenAll</c>)은 엔드포인트가 소유하고, 여기서는 비교/스코어링만 한다.
/// </summary>
public static class DualCheckEvaluator
{
    /// <param name="reviewThreshold">
    /// 신뢰도 검토 임계값(서버 레버). 0(기본)이면 신뢰도 게이팅 비활성 — 불일치만 검토 유발.
    /// </param>
    public static DualCheckResult Evaluate(
        InspectionResult vlm, MlClassification ml, double reviewThreshold,
        IConfidenceCalibrator calibrator)
    {
        // 일치 = 대소문자 무시 문자열 비교(class-agnostic).
        var agreement = string.Equals(
            ml.Label, vlm.Verdict.ToString(), StringComparison.OrdinalIgnoreCase);

        // A3: 비교 불가능한 척도를 per-source 표준화 후 게이팅(콜드스타트엔 VLM 제외=1.0).
        var vlmStd = calibrator.Standardize(ConfidenceSource.Vlm, vlm.Confidence);
        var mlStd = calibrator.Standardize(ConfidenceSource.Ml, ml.Confidence);
        var lowConfidence = reviewThreshold > 0 && (vlmStd < reviewThreshold || mlStd < reviewThreshold);

        return new DualCheckResult
        {
            MlLabel = ml.Label,
            MlConfidence = ml.Confidence,  // raw 유지(표준화는 내부 게이팅에만)
            Agreement = agreement,
            RequiresReview = !agreement || lowConfidence,
        };
    }
}
