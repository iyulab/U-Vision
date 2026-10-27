using System.Security.Cryptography;

namespace UVision.Api.Services.Memory;

/// <summary>
/// Mock 임베더 — 인코더 없이 기억 단의 plumbing 을 구동하는 결정론적 구현(테스트 · 데모).
/// <para>
/// ⚠️ 정직성: 이미지 내용을 보지 않는다. 바이트 해시로 벡터를 만들므로 <b>같은 바이트</b>만 가깝고 비슷한 이미지는
/// 가깝지 않다. 이웃 · 문턱의 실제 효과는 MLoop 인코더 + 실데이터로만 잴 수 있다.
/// </para>
/// </summary>
public sealed class MockImageEmbedder : IImageEmbedder
{
    private const int Dimension = 16;

    /// <summary>mock 인코더의 판 — 고정.</summary>
    public static readonly EncoderIdentity Identity = new("mock", "1", null, Dimension, Normalized: true);

    public string Name => "mock";

    public bool IsEnabled => true;

    public bool SupportsPatches => true;

    /// <summary>mock 패치 격자 — 2 × 2(바이트 해시에 패치 번호를 섞어 만든 벡터; 위치 · 내용 의미는 없다).</summary>
    public static readonly PatchGrid Grid = new(2, 2, 14, 28);

    public Task<PatchEmbeddings> EmbedPatchesAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default)
    {
        var features = images.Select(image =>
        {
            var global = VectorOf(image.Span, -1);
            var patches = new float[Grid.Count * Dimension];
            for (var p = 0; p < Grid.Count; p++)
                VectorOf(image.Span, p).CopyTo(patches, p * Dimension);
            return new ImageFeatures(global, patches);
        }).ToList();
        return Task.FromResult(new PatchEmbeddings(Identity, Grid, features));
    }

    private static float[] VectorOf(ReadOnlySpan<byte> image, int salt)
    {
        var digest = SHA256.HashData(salt < 0 ? image.ToArray() : [.. image, (byte)salt]);
        var v = new float[Dimension];
        for (var i = 0; i < Dimension; i++)
            v[i] = digest[i] - 127.5f;
        return VectorMath.Normalize(v);
    }

    public Task<ImageEmbeddings> EmbedAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default)
    {
        var vectors = images.Select(image => VectorOf(image.Span, -1)).ToList();
        return Task.FromResult(new ImageEmbeddings(Identity, vectors));
    }
}
