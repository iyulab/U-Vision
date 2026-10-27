using HoneAI;
using UVision.Api.Configuration;

namespace UVision.Api.Services.Memory;

/// <summary>
/// MLoop <c>mloop serve</c> 의 <c>POST /embed</c> 를 쓰는 임베더 — 프로세스 경계로만 소비한다(전용 ML 분류기와 같은 꼴,
/// HTTP 계약은 HoneAI <see cref="IMLoopClient"/>(<c>EmbedAsync</c>)가 소유). 이미지 <b>내용</b>을 base64 로 보내므로
/// MLoop 이 같은 호스트에 있을 필요가 없다.
/// <para>
/// 검증 범위: fake-handler 계약 테스트가 HoneAI 클라이언트를 거친 요청 · 응답을 가드한다. 실 <c>mloop serve</c> 0.45.0 +
/// <c>dinov2-small</c> 인코더로 라벨 12장 기억을 짓고 2장을 질의해 둘 다 맞는 클래스를 가장 가까운 이웃으로 찾았다
/// (2026-10-24, 합성 이미지 — 실 현장 사진 · 운영 규모의 문턱은 아직 측정 전). 패치 모드(<c>patches=true</c>)는 실 <c>mloop serve</c>
/// 0.46.0 + <c>dinov2-small</c> 로 엔진 왕복을 한 번 확인했다(2026-10-27, 공개 표본 100장 주입 — 기억이 지어지고 장부에 두 점수가
/// 쌓임; 지연 · CPU 는 미측정).
/// </para>
/// </summary>
public sealed class MloopImageEmbedder(IMLoopClient client, MemoryOptions options) : IImageEmbedder
{
    public string Name => "mloop";

    public bool IsEnabled => true;

    public bool SupportsPatches => true;

    public async Task<ImageEmbeddings> EmbedAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default)
    {
        if (images.Count is 0 or > IImageEmbedder.MaxBatch)
            throw new ArgumentOutOfRangeException(nameof(images), images.Count, $"1~{IImageEmbedder.MaxBatch} 장");

        var embedding = await CallAsync(new MLoopEmbeddingRequest(images, options.Encoder), cancellationToken);
        var identity = IdentityOf(embedding.Encoder);
        CheckVectors(embedding.Vectors, identity);
        return new ImageEmbeddings(identity, Normalized(embedding.Vectors, embedding.Encoder));
    }

    public async Task<PatchEmbeddings> EmbedPatchesAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default)
    {
        if (images.Count is 0 or > IImageEmbedder.MaxPatchBatch)
            throw new ArgumentOutOfRangeException(nameof(images), images.Count, $"1~{IImageEmbedder.MaxPatchBatch} 장");

        var embedding = await CallAsync(new MLoopEmbeddingRequest(images, options.Encoder, Patches: true), cancellationToken);
        var identity = IdentityOf(embedding.Encoder);
        var grid = embedding.Grid
            ?? throw new InvalidOperationException("mloop /embed?patches=true 가 격자를 주지 않았다(MLoop 0.46.0 미만?)");
        var patchGrid = new PatchGrid(grid.Rows, grid.Columns, grid.PatchSize, grid.CropSize);
        if (embedding.Patches is not { } patches || patches.Count != images.Count || embedding.Vectors.Count != images.Count)
            throw new InvalidOperationException("mloop /embed?patches=true 의 패치 수가 이미지 수와 다르다");

        CheckVectors(embedding.Vectors, identity);
        var globals = Normalized(embedding.Vectors, embedding.Encoder);
        var features = new List<ImageFeatures>(images.Count);
        for (var i = 0; i < images.Count; i++)
        {
            var vectors = patches[i].Vectors;
            if (vectors.Count != patchGrid.Count)
                throw new InvalidOperationException($"mloop 패치 {vectors.Count}개 ≠ 격자 {patchGrid.Rows}×{patchGrid.Columns}");
            CheckVectors(vectors, identity);
            var flat = new float[vectors.Count * identity.Dimension];
            for (var p = 0; p < vectors.Count; p++)
                (embedding.Encoder.Normalized ? vectors[p] : VectorMath.Normalize(vectors[p])).CopyTo(flat, p * identity.Dimension);
            features.Add(new ImageFeatures(globals[i], flat));
        }
        return new PatchEmbeddings(identity, patchGrid, features);
    }

    private async Task<MLoopEmbedding> CallAsync(MLoopEmbeddingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await client.EmbedAsync(request, cancellationToken);
        }
        catch (MLoopClientException ex)
        {
            // 400(몇 번째 장을 못 읽음) · 409(인코더 · 런타임 미설치 — 본문에 설치 방법) · 413 — 서버 본문이 메시지에 있다.
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private static EncoderIdentity IdentityOf(MLoopEncoder e) =>
        new(e.Id, e.Version, e.Sha256, e.Dimension, Normalized: true, e.Preprocessor);

    private static void CheckVectors(IReadOnlyList<float[]> vectors, EncoderIdentity identity)
    {
        if (vectors.Any(v => v.Length != identity.Dimension || v.Any(x => !float.IsFinite(x))))
            throw new InvalidOperationException("mloop /embed 벡터의 차원이 encoder.dimension 과 다르거나 유한하지 않다");
    }

    // 경계에서 정규화를 보장한다 — 거리 계산(코사인 = 내적)이 그것을 전제한다.
    private static IReadOnlyList<float[]> Normalized(IReadOnlyList<float[]> vectors, MLoopEncoder encoder) =>
        encoder.Normalized ? vectors : [.. vectors.Select(VectorMath.Normalize)];
}
