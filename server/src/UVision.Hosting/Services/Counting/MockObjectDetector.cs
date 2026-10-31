using System.Security.Cryptography;

namespace UVision.Api.Services.Counting;

/// <summary>
/// Mock 검출기 — 모델 없이 검출기 단의 plumbing 을 구동하는 결정론적 구현(테스트 · 데모).
/// <para>
/// ⚠️ 정직성: 이미지 내용을 보지 않는다. 바이트 해시로 상자 개수와 위치를 만들므로 <b>같은 바이트</b>만 같은 상자를 낸다.
/// 개수 정확도는 MLoop 검출 모델 + 정답 상자가 있는 실데이터로만 잴 수 있다.
/// </para>
/// </summary>
public sealed class MockObjectDetector : IObjectDetector
{
    /// <summary>mock 이 내는 상자의 종류.</summary>
    public const string ClassName = "object";

    /// <summary>mock 이 한 장에서 내는 상자 수의 상한.</summary>
    public const int MaxBoxes = 6;

    public string Name => "mock";

    public bool IsEnabled => true;

    public Task<DetectionSet> DetectAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
    {
        var digest = SHA256.HashData(image.Span);
        var boxes = new List<Detection>();
        var count = digest[0] % (MaxBoxes + 1);
        for (var i = 0; i < count; i++)
        {
            // 상자 하나에 해시 세 바이트: 위치 · 신뢰도. 겹침이 없게 가로로 칸을 나눠 놓는다.
            var cell = 1.0 / MaxBoxes;
            boxes.Add(new Detection(
                ClassName, X: i * cell + 0.01, Y: 0.1 + digest[1 + i] % 50 / 100.0, W: cell - 0.02, H: 0.3,
                Confidence: 0.5 + digest[8 + i] % 50 / 100.0));
        }
        return Task.FromResult(new DetectionSet(boxes));
    }
}
