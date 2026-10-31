namespace UVision.Api.Services.Counting;

/// <summary>
/// 검출기가 한 장에서 낸 상자 전부(신뢰도 문턱 전) — 세는 일은 <see cref="CountEvaluator"/> 가 한다.
/// </summary>
/// <param name="Boxes">상자. 좌표는 원본 이미지 기준 비율(0~1)이다.</param>
/// <param name="ModelSha256">상자를 낸 모델 파일의 SHA-256(서버가 알려 줄 때만) — 개수가 어느 모델의 것인지 남기는 출처.</param>
public sealed record DetectionSet(IReadOnlyList<Detection> Boxes, string? ModelSha256 = null);

/// <summary>
/// 객체 검출 경계(계수의 검출기 단). 앱 코드는 이 인터페이스만 안다 — 구체 구현(none/mock/mloop)은
/// <see cref="ObjectDetectorFactory"/> 가 설정에 따라 고른다. <see cref="Ml.IMlClassifier"/> · <see cref="Memory.IImageEmbedder"/> 와 같은 꼴이다.
/// </summary>
public interface IObjectDetector
{
    /// <summary>구현 식별자(none/mock/mloop).</summary>
    string Name { get; }

    /// <summary>검출할 수 있는가. false 면 <see cref="DetectAsync"/> 호출 금지 — 검출기 단이 없다.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 이미지 한 장의 상자를 낸다. 실패는 예외로 알린다 — 호출 측이 «개수를 모른다» 로 다룬다(개수를 모르는 채 OK 를 내지 않는다).
    /// 못 찾으면 빈 목록(실패가 아니다).
    /// </summary>
    Task<DetectionSet> DetectAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default);
}
