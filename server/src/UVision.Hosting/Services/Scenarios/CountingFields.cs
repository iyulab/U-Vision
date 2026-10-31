using UVision.Api.Models;

namespace UVision.Api.Services.Scenarios;

/// <summary>
/// 계수 필드를 요청과 저장된 시나리오에서 합친 모양 — 경계 검증과 저장이 같은 합침을 쓴다(둘이 다르게 합치면 검증을 통과한
/// 모양과 저장되는 모양이 갈린다).
/// </summary>
internal readonly record struct CountingFields(
    int? ExpectedCount,
    int? CountTolerance,
    Dictionary<string, int>? ExpectedByClass,
    string? ExtraPolicy,
    double? MinConfidence,
    List<string>? CountClasses)
{
    /// <param name="typeChanged">유형이 바뀌면 저장된 값은 옮기지 않는다(계수에서 검사로 바꿨는데 기대가 남지 않게).</param>
    public static CountingFields Merge(ScenarioInput input, Scenario? existing, bool typeChanged)
    {
        var kept = typeChanged ? null : existing;
        // 기대의 두 모양(총 개수 · 종류별)은 서로 바꿔 줄 수 있다 — 한쪽을 새로 주면 다른 쪽과 그에 묶인 필드는 보존하지 않는다.
        // 그러지 않으면 한쪽을 지울 길이 없어(생략 = 보존) 모양을 바꾸는 요청이 영영 거절된다. 같은 요청에 둘을 함께 주면
        // 둘 다 남아 검증이 거절한다.
        var givesTotal = input.ExpectedCount is not null;
        var givesByClass = input.ExpectedByClass is not null;
        return new(
            input.ExpectedCount ?? (givesByClass ? null : kept?.ExpectedCount),
            input.CountTolerance ?? (givesByClass ? null : kept?.CountTolerance),
            input.ExpectedByClass ?? (givesTotal ? null : kept?.ExpectedByClass),
            input.ExtraPolicy ?? (givesTotal ? null : kept?.ExtraPolicy),
            input.MinConfidence ?? kept?.MinConfidence,
            input.CountClasses ?? kept?.CountClasses);
    }
}
