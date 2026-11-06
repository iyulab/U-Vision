using UVision.Api.Models;

namespace UVision.Api.Services.Authority;

/// <summary>
/// 격상에 명시 확인이 필요한가 — 순수 함수. 근거 없이 판정 권한이 넘어가는 격상이 조용히 통과하지 않게 한다. 확정은 여전히 사람이고(PIN),
/// 이 판정은 «알고 하는가» 를 기록하게 할 뿐 막지 않는다(서면 하드 게이트는 관문 통계량 코드와 함께).
/// </summary>
public static class AuthorityPromotionGuard
{
    public const string SkippedStage = "skipped_stage";
    public const string SignalUnmet = "signal_unmet";
    public const string SignalInsufficient = "signal_insufficient";

    /// <summary>
    /// ML 이 판정 권한을 받는 단계인가 — 격상 신호가 읽는 증거는 ML 의 판정 품질이라 그 앞 단계(<c>advisory</c> — ML 의견은 보이되 VLM 이
    /// 정한다)로 올릴 때는 근거가 걸리지 않는다.
    /// </summary>
    public static bool GivesMlAuthority(AuthorityStage target) => target >= AuthorityStage.CoPrimary;

    /// <summary>확인을 요구하는 이유들 — 비어 있으면 확인 없이 격상된다. <paramref name="eligible"/> 은 메트릭의 <c>promotion_eligible</c>.</summary>
    public static IReadOnlyList<string> UnverifiedBasis(AuthorityStage current, AuthorityStage target, bool? eligible)
    {
        var basis = new List<string>(2);
        // 단계를 건너뛰면 신호가 «적격» 이어도 중간 단계에서 쌓일 근거(그 단계의 운영 기록)를 건너뛴 것이다.
        if ((int)target - (int)current > 1) basis.Add(SkippedStage);
        if (GivesMlAuthority(target) && eligible != true)
            basis.Add(eligible == false ? SignalUnmet : SignalInsufficient);
        return basis;
    }

    /// <summary>409 응답의 설명 — 무엇이 확인을 요구하는지와 확인하는 방법.</summary>
    public static string Explain(IReadOnlyList<string> basis)
    {
        var parts = basis.Select(b => b switch
        {
            SkippedStage => "한 단계를 건너뛰는 격상입니다",
            SignalUnmet => "격상 신호가 «적격 아님» 입니다",
            SignalInsufficient => "격상 신호를 낼 만큼 표본이 쌓이지 않았습니다",
            _ => b,
        });
        return $"{string.Join(" · ", parts)}. 알고 격상하려면 요청에 acknowledge_unverified: true 를 더하세요 — 확인한 사실이 격상 이력에 남습니다.";
    }
}
