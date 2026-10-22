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
/// 검증 범위: MLoop.API 소스에서 확인한 계약을 fake-handler 계약 테스트가 가드하고, 실 <c>mloop serve</c>(v0.15.0)
/// + 학습된 이미지 분류 모델과의 왕복은 이미지 2장(VLM 과 일치 1 · 불일치 1)으로 확인했다. 운영 규모의 정확도·지연은
/// 아직 측정 전이다(메트릭 B3 가 운영 중에 잰다).
/// </para>
/// <para>
/// 제약: mloop /predict 는 이미지를 <b>파일 경로</b>(<c>ImagePath</c> 컬럼)로 받는다(bytes/base64 미지원).
/// 따라서 이미지 바이트를 임시 파일로 쓰고 절대경로를 전달한다 — <b>mloop serve 가 같은 호스트에서
/// 그 경로를 읽을 수 있어야 한다</b>(U-Vision 온프레미스 단일호스트 전제).
/// </para>
/// </summary>
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

        // mloop 는 파일 경로를 받으므로 임시 파일로 쓴다(같은 호스트 가정). 항상 정리.
        var tempPath = Path.Combine(
            Path.GetTempPath(), $"uvision-ml-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(tempPath, image.ToArray(), cancellationToken);
        try
        {
            // 입력 행: 학습 스키마의 이미지 컬럼 = 절대 경로(라벨은 서버가 dummy 주입).
            var rows = new[] { new Dictionary<string, string> { [_options.ImageColumn] = tempPath } };

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"predict?name={Uri.EscapeDataString(modelName)}")
            {
                Content = JsonContent.Create(rows),
            };
            if (!string.IsNullOrEmpty(_options.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
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
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// 예측 클래스의 신뢰도. MLoop 0.19.0+ 는 정규화 confidence ∈ [0,1] 를 계약으로 직접 준다
    /// (단일 권위 = MLoop <c>ConfidencePolicy</c>) — 있으면 그대로 소비한다. 서버가 줬으나 비유한이면
    /// 파생 폴백이 아닌 0(오염 응답의 신뢰도 주장은 신뢰하지 않음).
    /// 구버전 서버(confidence null)는 기존 파생으로 폴백: argmax 분류이므로 확률맵 최댓값,
    /// 없으면(이진 등) Score, 그것도 없으면 0. 이 폴백은 MLoop min 버전을 강제하기 전까지 삭제 금지
    /// (0.19.0 은 아직 [Unreleased] — 배포된 구버전 serve 와의 하위호환).
    /// </summary>
    private static double ConfidenceOf(PredictRow row)
    {
        if (row.Confidence is double c)
            return double.IsFinite(c) ? Math.Clamp(c, 0.0, 1.0) : 0.0;

        double raw = row.Probabilities is { Count: > 0 } probs ? probs.Values.Max() : (row.Score ?? 0.0);
        // trust boundary(A3): 외부 모델 응답의 비정상 값(NaN/Infinity/[0,1] 범위 밖)을 여기서 정규화한다.
        // calibrator 가 아니라 경계에서 차단해야 하류(calibrator·DualCheck 게이팅) 오염을 근본 방지(raw Scores 는 보존).
        return double.IsFinite(raw) ? Math.Clamp(raw, 0.0, 1.0) : 0.0;
    }

    // --- mloop /predict 응답 계약(camelCase, MLoop.API 소스 확인) ---------

    private sealed record PredictResponse
    {
        [JsonPropertyName("predictions")] public List<PredictRow>? Predictions { get; init; }
        [JsonPropertyName("task")] public string? Task { get; init; }
    }

    private sealed record PredictRow
    {
        [JsonPropertyName("predictedLabel")] public string? PredictedLabel { get; init; }
        [JsonPropertyName("probabilities")] public Dictionary<string, double>? Probabilities { get; init; }
        [JsonPropertyName("score")] public double? Score { get; init; }
        // MLoop 0.19.0+ 정규화 신뢰도(nullable additive — 구버전 serve 는 null).
        [JsonPropertyName("confidence")] public double? Confidence { get; init; }
    }
}
