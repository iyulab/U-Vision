namespace UVision.Api.Storage;

/// <summary>
/// 확정 사례 기억의 벡터 캐시 — 인코더 판(<c>encoderKey</c>)마다 <c>image_id</c> → 벡터. 이미지 · 라벨 정본에서 다시
/// 만들 수 있는 파생물이다. 클래스는 담지 않는다(읽는 쪽이 현재 사람 라벨에서 잇는다).
/// </summary>
public interface IMemoryVectorStore
{
    /// <summary>벡터를 덧붙인다(append-only). 같은 <c>image_id</c> 가 다시 오면 읽을 때 나중 것이 이긴다.</summary>
    Task AppendAsync(
        string scenarioId, string encoderKey, IReadOnlyList<MemoryVector> vectors,
        CancellationToken cancellationToken = default);

    /// <summary>판의 벡터 전부(<c>image_id</c> 별 마지막 것). 없으면 빈 맵.</summary>
    Task<IReadOnlyDictionary<string, float[]>> ReadAsync(
        string scenarioId, string encoderKey, CancellationToken cancellationToken = default);
}

/// <summary>벡터 캐시 한 줄.</summary>
public sealed record MemoryVector(string ImageId, float[] Vector);
