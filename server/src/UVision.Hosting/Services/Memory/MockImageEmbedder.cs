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

    public Task<ImageEmbeddings> EmbedAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default)
    {
        var vectors = images.Select(image =>
        {
            var digest = SHA256.HashData(image.Span);
            var v = new float[Dimension];
            for (var i = 0; i < Dimension; i++)
                v[i] = digest[i] - 127.5f;
            return VectorMath.Normalize(v);
        }).ToList();
        return Task.FromResult(new ImageEmbeddings(Identity, vectors));
    }
}
