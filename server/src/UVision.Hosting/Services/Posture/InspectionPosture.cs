namespace UVision.Api.Services.Posture;

/// <summary>운영 자세 등급(③.5 E2 + A1). 사다리 평가(AuthorityLadder)의 산출.</summary>
public enum InspectionPosture
{
    /// <summary>정상 진행 — verdict 자동 확정.</summary>
    Proceed,
    /// <summary>불일치/저신뢰 → 비차단 '검토 필요' 밴드(advisory).</summary>
    ReviewHold,
    /// <summary>불일치(co-primary↑) → 차단형 확인 게이트(작업자 명시 확인 전 진행 차단, A1).</summary>
    ReviewBlock,
    /// <summary>주 검출원 사용 불가 → 자동 판정 없음(NG-safe fail-closed).</summary>
    FailClosed,
}
