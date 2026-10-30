namespace UVision.Api.Configuration;

/// <summary>
/// 권한 이양 사다리 레버(A1). 호스트 config 섹션 <c>UVision:Authority</c>. 시나리오 무관 서버 레버
/// (샘플 정조준 아님). 격상 자격 신호·자동격하 발동 임계의 단일 출처.
/// </summary>
public sealed class AuthorityOptions
{
    /// <summary>격상 자격 판정 최소 표본(rolling window). 미만이면 promotion_eligible=null.</summary>
    public int MinWindow { get; set; } = 50;

    /// <summary>
    /// 격상 자격 판정에 필요한 사람 라벨 NG 최소 건수 — 재현율의 분모(<c>labeled_ng</c> · <c>ml_ng_scored</c>) 각각.
    /// 미만이면 <c>promotion_eligible=null</c>(1/1 이 «재현율 1.0» 으로 읽히지 않게). 기본 59 는 «놓침 0건이어도 재현율 ≥ 0.95 를
    /// 95 % 신뢰로 주장하려면 NG 약 59건»(0.95^59 ≈ 0.048)에서 온다. 이 신호는 이행기 신호다 — 사전 등록 관문의
    /// 클래스별 신뢰 하한(설계 정본)이 지어지면 대체한다.
    /// </summary>
    public int MinNgSamples { get; set; } = 59;

    /// <summary>ML NG recall 바닥. 격상 자격: ml_ng_recall ≥ 이 값.</summary>
    public double RecallFloor { get; set; } = 0.95;

    /// <summary>agreement rate 바닥. 격상 자격: agreement_rate ≥ 이 값.</summary>
    public double AgreementFloor { get; set; } = 0.9;

    /// <summary>자동격하 안전체크 주기 — inspect N건마다 1회 재집계(암종화·핫패스 보호).</summary>
    public int AutoDemoteCheckEvery { get; set; } = 25;
}
