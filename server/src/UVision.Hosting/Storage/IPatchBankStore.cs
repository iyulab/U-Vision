namespace UVision.Api.Storage;

/// <summary>
/// 정상 사례의 패치 벡터 캐시 — 인코더 판(<c>encoderKey</c>)마다 <c>image_id</c> → 패치 벡터(패치 수 × 차원을 이어 붙인
/// float 배열). 이미지 정본에서 다시 만들 수 있는 파생물이다(벡터 캐시와 같다).
/// </summary>
public interface IPatchBankStore
{
    /// <summary>패치 벡터를 쓴다(원자적 — 읽는 쪽이 반쪽 파일을 보지 않는다).</summary>
    Task WriteAsync(
        string scenarioId, string encoderKey, string imageId, float[] patches, CancellationToken cancellationToken = default);

    /// <summary>패치 벡터를 읽는다. 없거나 <paramref name="expectedLength"/> 와 다르면(끊긴 파일) null.</summary>
    Task<float[]?> ReadAsync(
        string scenarioId, string encoderKey, string imageId, int? expectedLength = null,
        CancellationToken cancellationToken = default);
}
