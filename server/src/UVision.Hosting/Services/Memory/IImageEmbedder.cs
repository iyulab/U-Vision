namespace UVision.Api.Services.Memory;

/// <summary>
/// 임베딩을 만든 인코더의 판 — 같은 판에서 나온 벡터끼리만 거리를 잴 수 있다.
/// </summary>
/// <param name="Id">인코더 id.</param>
/// <param name="Version">인코더 판 표기.</param>
/// <param name="Sha256">가중치 파일의 SHA-256(없을 수 있다).</param>
/// <param name="Dimension">벡터 차원.</param>
/// <param name="Normalized">벡터가 L2 정규화돼 있는가(그러면 코사인 = 내적).</param>
/// <param name="Preprocessor">전처리 판(MLoop 0.46.0+, 예: <c>pillow/2</c>) — 가중치가 같아도 전처리가 바뀌면 벡터가 바뀐다. 없을 수 있다.</param>
public sealed record EncoderIdentity(
    string Id, string Version, string? Sha256, int Dimension, bool Normalized, string? Preprocessor = null)
{
    /// <summary>
    /// 기억의 판 키 — 인코더 id + 가중치 SHA-256 앞 12자(+ 전처리 판이 있으면 그것). 같은 id 라도 가중치나 전처리가 바뀌면 다른
    /// 판이다. SHA-256 이 없으면 판 표기로 대신한다. 디렉터리 이름이 되므로 쓰는 곳(<see cref="Storage.StoragePaths"/>)이 형식을 검사한다.
    /// </summary>
    public string Key
    {
        get
        {
            var weights = Sha256 is { Length: >= 12 } sha ? $"{Id}-{sha[..12].ToLowerInvariant()}" : $"{Id}-{Version}";
            // 같은 가중치라도 전처리가 바뀌면(알파 처리 등) 벡터가 달라진다 — 그 판은 다른 공간이다. 경로가 되므로 안전한 글자만.
            return Preprocessor is { Length: > 0 } pre
                ? $"{weights}-{string.Concat(pre.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'))}"
                : weights;
        }
    }
}

/// <summary>이미지 여러 장의 임베딩 — 요청 순서대로 한 장에 벡터 하나.</summary>
public sealed record ImageEmbeddings(EncoderIdentity Encoder, IReadOnlyList<float[]> Vectors);

/// <summary>패치 격자 — 인코더가 이미지(중앙 crop)를 <c>Rows × Columns</c> 패치로 나눈 모양.</summary>
/// <param name="PatchSize">패치 한 변(px, crop 안).</param>
/// <param name="CropSize">격자가 덮는 정사각 crop 의 한 변(px).</param>
public sealed record PatchGrid(int Rows, int Columns, int PatchSize, int CropSize)
{
    public int Count => Rows * Columns;
}

/// <summary>이미지의 한 영역(자기 픽셀 기준): 왼쪽 · 위 · 오른쪽 · 아래.</summary>
public sealed record ImageRegion(double X0, double Y0, double X1, double Y1);

/// <summary>
/// 이미지 한 장의 특징 — 전역 벡터 하나와 패치 벡터들(행 우선으로 한 배열에 이어 붙임, 패치마다 <c>Dimension</c> 개).
/// <paramref name="Crop"/> 은 격자가 덮은 영역(원본 픽셀) — 패치 (r, c) 의 위치를 원본 위에 그리는 데 쓴다. 모르면 null.
/// </summary>
public sealed record ImageFeatures(float[] Global, float[] Patches, ImageRegion? Crop = null);

/// <summary>이미지 여러 장의 전역 · 패치 특징 — 요청 순서대로.</summary>
public sealed record PatchEmbeddings(EncoderIdentity Encoder, PatchGrid Grid, IReadOnlyList<ImageFeatures> Images);

/// <summary>
/// 이미지 임베딩 경계(system 1 확정 사례 기억). 앱 코드는 이 인터페이스만 안다 — 구체 구현(none/mock/mloop)은
/// <see cref="ImageEmbedderFactory"/> 가 설정에 따라 고른다. <see cref="Ml.IMlClassifier"/> 와 같은 꼴이다.
/// </summary>
public interface IImageEmbedder
{
    /// <summary>한 번에 보낼 수 있는 최대 장수.</summary>
    const int MaxBatch = 64;

    /// <summary>패치 모드로 한 번에 보낼 수 있는 최대 장수 — 이미지당 응답이 크다(MLoop: 이미지당 JSON ~1 MB).</summary>
    const int MaxPatchBatch = 8;

    /// <summary>구현 식별자(none/mock/mloop).</summary>
    string Name { get; }

    /// <summary>임베딩을 만들 수 있는가. false 면 <see cref="EmbedAsync"/> 호출 금지 — 기억 단이 없다.</summary>
    bool IsEnabled { get; }

    /// <summary>패치 모드를 지원하는가(<see cref="EmbedPatchesAsync"/> 호출 가능). 기본은 아니오 — 구현이 켠다.</summary>
    bool SupportsPatches => false;

    /// <summary>
    /// 이미지 1~<see cref="MaxPatchBatch"/> 장의 전역 · 패치 특징을 한 번에 낸다(같은 호출의 같은 run — 전역은
    /// <see cref="EmbedAsync"/> 와 같은 값이다). <paramref name="encoder"/> 가 있으면 설정의 인코더 대신 그것으로 패치 특징을 낸다(시나리오가
    /// 고른 패치 인코더 — 전역 기억은 언제나 설정의 인코더라 <see cref="EmbedAsync"/> 에는 이 인자가 없다). 실패는 예외로 알린다. <see cref="SupportsPatches"/> 가 false 면 호출 금지.
    /// </summary>
    Task<PatchEmbeddings> EmbedPatchesAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, string? encoder = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"임베더 {Name} 는 패치 모드를 지원하지 않는다");

    /// <summary>이미지 1~<see cref="MaxBatch"/> 장을 임베딩한다. 실패는 예외로 알린다(호출 측이 기권으로 흡수).</summary>
    Task<ImageEmbeddings> EmbedAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default);
}
