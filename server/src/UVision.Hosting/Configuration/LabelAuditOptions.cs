namespace UVision.Api.Configuration;

/// <summary>
/// 라벨 감사(C1) 구성 — 블라인드 재라벨 표본율. config 섹션 <c>UVision:LabelAudit</c>.
/// per-scenario 레버는 수요 입증 시(reference_cap 패턴) — 현재 전역 상수 기본.
/// </summary>
public sealed class LabelAuditOptions
{
    /// <summary>라벨된(미감사) 건 중 블라인드 재감사 표본 비율(%). 0=감사 끔, 100=전수.</summary>
    public int SampleRatePercent { get; init; } = 10;
}
