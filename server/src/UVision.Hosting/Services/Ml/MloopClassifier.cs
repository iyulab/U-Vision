using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using UVision.Api.Configuration;
using UVision.Api.Services.Models;

namespace UVision.Api.Services.Ml;

/// <summary>
/// MLoop <c>mloop serve</c> REST(<c>POST /predict</c>) 소비 ML 분류기 — 신뢰성 플라이휠 ②의 추론 경계.
/// MLoop 의 소비 표면은 <c>mloop serve</c> 의 HTTP API 다 — 라이브러리로 끌어오지 않고 <b>프로세스 경계로 디커플</b>한다.
///
/// <para>
/// 이미지는 <b>내용</b>(base64 <c>{"images": [...]}</c>)으로 보낸다 — MLoop 0.45.0+ 계약. 그래서 mloop serve 가
/// 같은 호스트에 있을 필요가 없다(경로 행 입력은 클라이언트와 서버가 디스크를 공유해야 했다).
/// </para>
/// <para>
/// 검증 범위: MLoop 0.45.0 소스에서 확인한 계약을 fake-handler 계약 테스트가 가드한다. 내용 입력으로 실
/// <c>mloop serve</c> 와 왕복한 것은 아직 없다(UNVERIFIED — 경로 입력 시절 v0.15.0 왕복 2장만 있었다).
/// </para>
/// </summary>
// TODO(upstream: HoneAI IMLoopClient 이미지 내용 예측) — 엔진은 MLoop 을 HoneAI HttpMLoopClient 로 부르기로 했으나
// 그 클라이언트가 아직 이미지 내용을 보내지 못한다. 보낼 수 있는 HoneAI 판이 나오면 그것으로 옮기고 이 손 HTTP 호출을 지운다.
public sealed class MloopClassifier : IMlClassifier
{
    private readonly HttpClient _http;
    private readonly MlOptions _options;
    private readonly ModelBindingResolver? _resolver;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public MloopClassifier(
        HttpClient http, MlOptions options, ModelBindingResolver? resolver = null)
    {
        _http = http;
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

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"predict?name={Uri.EscapeDataString(modelName)}")
        {
            Content = JsonContent.Create(new PredictRequest([Convert.ToBase64String(image.Span)])),
        };
        if (!string.IsNullOrEmpty(_options.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 400(못 읽는 이미지 · 이미지 분류가 아닌 모델) · 404(모델 없음) · 413 — 본문을 그대로 사유로.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"mloop /predict 실패 ({(int)response.StatusCode}): {body}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<PredictResponse>(
            JsonOpts, cancellationToken);
        var row = parsed?.Predictions is { Count: > 0 } p ? p[0] : null;
        if (row?.PredictedLabel is null)
            throw new InvalidOperationException("mloop /predict 응답에 예측 결과가 없습니다.");

        return new MlClassification
        {
            Label = row.PredictedLabel,
            Confidence = ConfidenceOf(row),
            Scores = row.Probabilities ?? new Dictionary<string, double>(),
            ModelVersion = binding?.Version,
        };
    }

    /// <summary>
    /// 예측 클래스의 신뢰도 — MLoop 이 계약으로 주는 정규화 confidence ∈ [0,1](단일 권위 = MLoop
    /// <c>ConfidencePolicy</c>, 분류는 이긴 클래스 확률). 이미지 내용 입력이 이미 MLoop 0.45.0+ 를 요구하므로
    /// 옛 서버용 파생 폴백은 두지 않는다. 없거나 비유한이면 0 — 신뢰도 주장이 없는 답을 믿지 않는다(trust boundary A3).
    /// </summary>
    private static double ConfidenceOf(PredictRow row) =>
        row.Confidence is double c && double.IsFinite(c) ? Math.Clamp(c, 0.0, 1.0) : 0.0;

    // --- mloop /predict 계약(camelCase, MLoop 0.45.0 소스 확인) ---------

    private sealed record PredictRequest([property: JsonPropertyName("images")] IReadOnlyList<string> Images);

    private sealed record PredictResponse
    {
        [JsonPropertyName("predictions")] public List<PredictRow>? Predictions { get; init; }
        [JsonPropertyName("task")] public string? Task { get; init; }
    }

    private sealed record PredictRow
    {
        [JsonPropertyName("predictedLabel")] public string? PredictedLabel { get; init; }
        [JsonPropertyName("probabilities")] public Dictionary<string, double>? Probabilities { get; init; }
        // 태스크에 따라 없을 수 있어 nullable — 이미지 분류에서는 MLoop 이 늘 채운다.
        [JsonPropertyName("confidence")] public double? Confidence { get; init; }
    }
}
