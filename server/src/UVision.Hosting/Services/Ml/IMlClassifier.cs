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

    /// <summary>판정에 사용된 모델 버전(B1, mloop active 바인딩 해석 시). 미등록/mock/none 이면 null.</summary>
    public string? ModelVersion { get; init; }

    /// <summary>
    /// <b>실제로 답한</b> 모델 파일의 SHA-256 — 서빙 쪽이 보고한 값이다(<see cref="ModelVersion"/> 은 엔진 레지스트리가 가리키는 판이고,
    /// 그것이 가리키는 이름 아래 서빙 슬롯이 무엇인지는 모른다). 실험 id 는 프로젝트마다 <c>exp-001</c> 로 겹치므로 신원은 해시다.
    /// 보고하지 않는 서버 · mock · none 이면 null.
    /// </summary>
    public string? ModelSha256 { get; init; }

    /// <summary>
    /// 이 사진의 «불량 점수» — 문턱 · 작동점에 기대지 않는 순위 지표(AUROC · 오경보 고정 재현율)의 입력이다.
    /// <paramref name="isNg"/> 를 주면(불량 유형을 나누는 시나리오) 그 규칙이 NG 로 읽는 클래스들의 확률 합. 주지 않으면 이진(OK/NG)
    /// 가정이다: 클래스별 확률의 <c>NG</c> 쪽(대소문자 무시), 없으면 <c>1 − OK</c>, 확률 맵이 비었으면 예측 라벨과 그 확률로 환산한다.
    /// 읽을 수 없으면 null — 그 사진은 순위 지표에서 빠진다.
    /// </summary>
    public double? NgScore(Func<string, bool>? isNg = null)
    {
        if (isNg is not null && Scores.Count > 0)
            return Math.Clamp(Scores.Where(s => isNg(s.Key)).Sum(s => s.Value), 0.0, 1.0);
        if (TryScore("NG", out var ng)) return ng;
        if (TryScore("OK", out var ok)) return 1 - ok;
        if (string.Equals(Label, "NG", StringComparison.OrdinalIgnoreCase)) return Confidence;
        if (string.Equals(Label, "OK", StringComparison.OrdinalIgnoreCase)) return 1 - Confidence;
        return null;
    }

    private bool TryScore(string cls, out double value)
    {
        foreach (var (k, v) in Scores)
            if (string.Equals(k, cls, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        value = 0;
        return false;
    }
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
