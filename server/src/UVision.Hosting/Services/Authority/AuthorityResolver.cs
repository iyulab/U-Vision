using UVision.Api.Models;

namespace UVision.Api.Services.Authority;

/// <summary>
/// 저장된 단계 상태(또는 부재)를 운영 단계로 환원하는 순수 함수(A1). I/O·예외 흡수는 호출측(inspect)이
/// 소유한다(<see cref="Services.DualCheck.DualCheckEvaluator"/> 동일 규율). 부재 = Advisory(현재 동작).
/// </summary>
public static class AuthorityResolver
{
    public static AuthorityStage Resolve(AuthorityState? state) => state?.Stage ?? AuthorityStage.Advisory;
}
