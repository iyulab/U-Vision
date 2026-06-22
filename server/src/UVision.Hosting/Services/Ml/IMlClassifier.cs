namespace UVision.Api.Services.Ml;

/// <summary>
/// 전용 ML 비전 분류기의 단건 예측 결과 — provider 무관 도메인 객체.
/// 라벨은 <b>string</b>(클래스 식별자, 예: "ok"/"ng") — <c>Verdict</c> enum 을 재사용하지 않는다
/// (다중분류 확장 대비, 라벨 사이드카와 동일한 class-agnostic 원칙).
/// </summary>
public sealed record MlClassification
{
    /// <summary>예측 클래스(데이터셋 폴더명 = MLoop class label).</summary>
    public required string Label { get; init; }

    /// <summary>예측 클래스의 확률(0.0~1.0).</summary>
    public required double Confidence { get; init; }

    /// <summary>클래스별 확률(있으면). 없으면 빈 맵.</summary>
    public IReadOnlyDictionary<string, double> Scores { get; init; } =
        new Dictionary<string, double>();
}

/// <summary>
/// 전용 ML 비전 분류기 경계(신뢰성 플라이휠 ②~③). 앱 코드는 이 인터페이스만 안다.
/// 구체 구현(none/mock/mloop)은 <see cref="MlClassifierFactory"/> 가 설정에 따라 주입한다.
/// <para>
/// <see cref="IsEnabled"/> 가 false 면(아직 전용 모델 없음 — 플라이휠 ① 단계) 호출 측은
/// VLM 단독 경로로 동작한다. ML+VLM 2중체크(③)는 enabled 일 때만 활성화된다.
/// </para>
/// 의도적으로 <c>IVlmProvider</c> 와 병렬 형태다 — 두 판정원을 같은 추상도로 다루기 위함.
/// </summary>
public interface IMlClassifier
{
    /// <summary>구현 식별자(none/mock/mloop).</summary>
    string Name { get; }

    /// <summary>전용 모델이 준비돼 분류 가능한가. false 면 <see cref="ClassifyAsync"/> 호출 금지.</summary>
    bool IsEnabled { get; }

    /// <summary>이미지 1장을 시나리오의 전용 모델로 분류한다.</summary>
    Task<MlClassification> ClassifyAsync(
        ReadOnlyMemory<byte> image, string scenarioId, CancellationToken cancellationToken = default);
}
