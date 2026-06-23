namespace UVision.Api.Configuration;

/// <summary>
/// 권한 이양 사다리 레버(A1). 호스트 config 섹션 <c>UVision:Authority</c>. 시나리오 무관 서버 레버
/// (샘플 정조준 아님). 격상 자격 신호·자동격하 발동 임계의 단일 출처.
/// </summary>
public sealed class AuthorityOptions
{
    /// <summary>격상 자격 판정 최소 표본(rolling window). 미만이면 promotion_eligible=null.</summary>
    public int MinWindow { get; set; } = 50;

    /// <summary>ML NG recall 바닥. 격상 자격: ml_ng_recall ≥ 이 값.</summary>
    public double RecallFloor { get; set; } = 0.95;

    /// <summary>agreement rate 바닥. 격상 자격: agreement_rate ≥ 이 값.</summary>
    public double AgreementFloor { get; set; } = 0.9;

    /// <summary>자동격하 안전체크 주기 — inspect N건마다 1회 재집계(암종화·핫패스 보호).</summary>
    public int AutoDemoteCheckEvery { get; set; } = 25;
}
