namespace UVision.Api.Services.Counting;

/// <summary>검출기 없음(<c>none</c>, 기본) — 검출기 단이 없다. 호출하면 설정 오류다.</summary>
public sealed class DisabledObjectDetector : IObjectDetector
{
    public string Name => "none";

    public bool IsEnabled => false;

    public Task<DetectionSet> DetectAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("검출기가 꺼져 있다(UVision:Detection:Provider=none)");
}
