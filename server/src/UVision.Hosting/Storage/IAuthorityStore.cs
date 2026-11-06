using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// per-scenario 권한 단계 상태 저장소(A1). B1 <see cref="IModelRegistry"/> 의 포인터 패턴과 동형이되
/// 버전 레지스트리 없음(단계 = enum). 부재 = Advisory(기본) — 해석은 AuthorityResolver.
/// </summary>
public interface IAuthorityStore
{
    /// <summary>현재 상태. 파일 없으면 null(정직 — 기본값 주입은 resolver).</summary>
    Task<AuthorityState?> ReadAsync(string scenarioId, CancellationToken ct = default);

    /// <summary>단계를 설정한다 — previous = 직전 단계(없으면 Advisory baseline), history append. atomic.</summary>
    /// <param name="unverifiedBasis">근거 없이 올린 격상이면 서버가 확인을 요구한 이유들 — 이력에 «확인함» 으로 남는다. 아니면 null.</param>
    Task SetStageAsync(
        string scenarioId, AuthorityStage stage, string by, string mode, string? reason,
        CancellationToken ct = default, IReadOnlyList<string>? unverifiedBasis = null);
}
