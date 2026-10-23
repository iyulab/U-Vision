namespace UVision.Api.Services.Memory;

/// <summary>
/// 임베딩을 만든 인코더의 판 — 같은 판에서 나온 벡터끼리만 거리를 잴 수 있다.
/// </summary>
/// <param name="Id">인코더 id.</param>
/// <param name="Version">인코더 판 표기.</param>
/// <param name="Sha256">가중치 파일의 SHA-256(없을 수 있다).</param>
/// <param name="Dimension">벡터 차원.</param>
/// <param name="Normalized">벡터가 L2 정규화돼 있는가(그러면 코사인 = 내적).</param>
public sealed record EncoderIdentity(string Id, string Version, string? Sha256, int Dimension, bool Normalized)
{
    /// <summary>
    /// 기억의 판 키 — 인코더 id + 가중치 SHA-256 앞 12자. 같은 id 라도 가중치가 바뀌면 다른 판이다. SHA-256 이 없으면
    /// 판 표기로 대신한다. 디렉터리 이름이 되므로 쓰는 곳(<see cref="Storage.StoragePaths"/>)이 형식을 검사한다.
    /// </summary>
    public string Key => Sha256 is { Length: >= 12 } sha
        ? $"{Id}-{sha[..12].ToLowerInvariant()}"
        : $"{Id}-{Version}";
}

/// <summary>이미지 여러 장의 임베딩 — 요청 순서대로 한 장에 벡터 하나.</summary>
public sealed record ImageEmbeddings(EncoderIdentity Encoder, IReadOnlyList<float[]> Vectors);

/// <summary>
/// 이미지 임베딩 경계(system 1 확정 사례 기억). 앱 코드는 이 인터페이스만 안다 — 구체 구현(none/mock/mloop)은
/// <see cref="ImageEmbedderFactory"/> 가 설정에 따라 고른다. <see cref="Ml.IMlClassifier"/> 와 같은 꼴이다.
/// </summary>
public interface IImageEmbedder
{
    /// <summary>한 번에 보낼 수 있는 최대 장수.</summary>
    const int MaxBatch = 64;

    /// <summary>구현 식별자(none/mock/mloop).</summary>
    string Name { get; }

    /// <summary>임베딩을 만들 수 있는가. false 면 <see cref="EmbedAsync"/> 호출 금지 — 기억 단이 없다.</summary>
    bool IsEnabled { get; }

    /// <summary>이미지 1~<see cref="MaxBatch"/> 장을 임베딩한다. 실패는 예외로 알린다(호출 측이 기권으로 흡수).</summary>
    Task<ImageEmbeddings> EmbedAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default);
}
