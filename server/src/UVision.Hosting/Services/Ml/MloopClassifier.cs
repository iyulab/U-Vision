using HoneAI;
using UVision.Api.Configuration;
using UVision.Api.Services.Models;

namespace UVision.Api.Services.Ml;

/// <summary>
/// MLoop <c>mloop serve</c>(<c>POST /predict</c>) 소비 ML 분류기 — 신뢰성 플라이휠 ②의 추론 경계.
/// MLoop 의 소비 표면은 <c>mloop serve</c> 의 HTTP API 다 — 라이브러리로 끌어오지 않고 <b>프로세스 경계로 디커플</b>하며,
/// 그 HTTP 계약은 HoneAI <see cref="IMLoopClient"/>(<c>PredictImagesAsync</c>)가 소유한다.
///
/// <para>
/// 이미지는 <b>내용</b>(base64)으로 보낸다 — MLoop 0.45.0+ 계약. 그래서 mloop serve 가 같은 호스트에 있을 필요가 없다.
/// </para>
/// <para>
/// 검증 범위: fake-handler 계약 테스트가 HoneAI 클라이언트를 거친 요청 · 응답을 가드한다. 내용 입력으로 실
/// <c>mloop serve</c> 0.45.0 + 합성 이미지로 학습한 2클래스 모델과의 <c>/inspect</c> 왕복을 2장(OK 1 · NG 1)으로
/// 확인했다(2026-10-24). 운영 규모의 정확도·지연은 아직 측정 전이다.
/// </para>
/// </summary>
public sealed class MloopClassifier : IMlClassifier
{
    private readonly IMLoopClient _client;
    private readonly MlOptions _options;
    private readonly ModelBindingResolver? _resolver;

    public MloopClassifier(
        IMLoopClient client, MlOptions options, ModelBindingResolver? resolver = null)
    {
        _client = client;
        _options = options;
        _resolver = resolver;
    }

    public string Name => "mloop";

    public bool IsEnabled => true;

    public async Task<MlClassification> ClassifyAsync(
        ReadOnlyMemory<byte> image, string scenarioId, CancellationToken cancellationToken = default)
    {
        // B1: active 바인딩 해석 → 모델명. 미등록/해석 실패 시 전역 MlOptions.Model 폴백(현재 동작 보존).
        var binding = _resolver is null ? null : await _resolver.ResolveAsync(scenarioId, cancellationToken);
        var modelName = binding?.ModelName ?? _options.Model;

        IReadOnlyList<ITracedPrediction<MLoopPredictionResult>> predictions;
        try
        {
            predictions = await _client.PredictImagesAsync(
                new MLoopImagePredictionRequest([image], modelName), cancellationToken);
        }
        catch (MLoopClientException ex)
        {
            // 400(못 읽는 이미지 · 이미지 분류가 아닌 모델) · 404(모델 없음) · 413 — 서버 본문이 메시지에 있다.
            throw new InvalidOperationException(ex.Message, ex);
        }

        var prediction = predictions[0];
        if (prediction.Value.Outputs.GetValueOrDefault("predictedLabel") is not string label)
            throw new InvalidOperationException("mloop /predict 응답에 예측 결과가 없습니다.");

        return new MlClassification
        {
            Label = label,
            Confidence = ConfidenceOf(prediction),
            Scores = ScoresOf(prediction.Value.Outputs),
            ModelVersion = binding?.Version,
        };
    }

    /// <summary>
    /// 예측 클래스의 신뢰도 — MLoop 이 계약으로 주는 정규화 confidence ∈ [0,1](단일 권위 = MLoop
    /// <c>ConfidencePolicy</c>, HoneAI 가 clamp · 비유한 0 으로 옮긴다). 행에 <c>confidence</c> 가 없으면 0 — HoneAI 는 옛
    /// 서버용으로 확률 최댓값을 파생하지만, 이미지 내용 입력이 이미 MLoop 0.45.0+ 를 요구하고 그 판은 늘 confidence 를
    /// 준다. 신뢰도 주장이 없는 답을 믿지 않는다(trust boundary A3).
    /// </summary>
    private static double ConfidenceOf(ITracedPrediction<MLoopPredictionResult> prediction) =>
        prediction.Value.Outputs.GetValueOrDefault("confidence") is long or double
            ? prediction.Provenance.Confidence ?? 0.0
            : 0.0;

    private static Dictionary<string, double> ScoresOf(IReadOnlyDictionary<string, object?> outputs)
    {
        var scores = new Dictionary<string, double>();
        if (outputs.GetValueOrDefault("probabilities") is IReadOnlyDictionary<string, object?> probabilities)
            foreach (var (cls, value) in probabilities)
                if (value is long or double)
                    scores[cls] = Convert.ToDouble(value);
        return scores;
    }
}
