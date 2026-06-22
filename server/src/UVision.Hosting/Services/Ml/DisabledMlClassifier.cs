namespace UVision.Api.Services.Ml;

/// <summary>
/// 전용 ML 모델이 아직 없는 상태(플라이휠 ① 단계 — VLM 단독). 기본값.
/// <see cref="IsEnabled"/>=false 로 호출 측이 2중체크를 건너뛰게 한다.
/// </summary>
public sealed class DisabledMlClassifier : IMlClassifier
{
    public string Name => "none";

    public bool IsEnabled => false;

    public Task<MlClassification> ClassifyAsync(
        ReadOnlyMemory<byte> image, string scenarioId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "ML 분류기가 비활성(provider=none)입니다. IsEnabled 를 먼저 확인하세요.");
}
