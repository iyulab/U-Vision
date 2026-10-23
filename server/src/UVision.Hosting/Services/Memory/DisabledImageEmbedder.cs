namespace UVision.Api.Services.Memory;

/// <summary>기억 단 없음(기본) — 판정은 지금과 같다.</summary>
public sealed class DisabledImageEmbedder : IImageEmbedder
{
    public string Name => "none";

    public bool IsEnabled => false;

    public Task<ImageEmbeddings> EmbedAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("기억 단이 꺼져 있다(UVision:Memory:Provider=none).");
}
