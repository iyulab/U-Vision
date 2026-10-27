using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>검사 한 건의 이상 지도 사이드카 — 판정 당시 정상 기억의 값이라 나중에 다시 계산하면 달라질 수 있다.</summary>
public interface IAnomalyMapStore
{
    Task WriteAsync(
        string scenarioId, string date, string imageId, AnomalyMap map, CancellationToken cancellationToken = default);

    /// <summary>없으면 null. 형식 위반 id · 날짜는 <see cref="ArgumentException"/>.</summary>
    Task<AnomalyMap?> ReadAsync(
        string scenarioId, string date, string imageId, CancellationToken cancellationToken = default);
}
