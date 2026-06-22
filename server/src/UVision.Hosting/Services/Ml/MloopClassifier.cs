using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using UVision.Api.Configuration;
using UVision.Api.Services.Models;

namespace UVision.Api.Services.Ml;

/// <summary>
/// MLoop <c>mloop serve</c> REST(<c>POST /predict</c>) 소비 ML 분류기 — 신뢰성 플라이휠 ②의 추론 경계.
/// MLoop core 는 NuGet 미발행 + ProjectReference 금지(글로벌 정책)이므로 <b>HTTP 경계로 디커플</b>한다.
///
/// <para>
/// ⚠️ <b>UNVERIFIED(실서버 미검증)</b>: 본 클라이언트는 MLoop.API 소스(<c>Program.cs</c>/<c>PredictionService</c>)에서
/// 확인한 계약에 맞춰 작성됐고 fake-handler 계약 테스트로 검증되나, <b>실 mloop serve + 학습된 모델</b> 왕복은
/// FW-3 정확도 스파이크(데이터/런타임 게이트)에서만 검증된다. IronHiveVlmProvider 의 UNVERIFIED 와 동일 규율.
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
    /// 예측 클래스의 신뢰도. argmax 분류이므로 확률맵의 최댓값이 예측 클래스 확률이다.
    /// 확률맵이 없으면(이진 등) Score, 그것도 없으면 0.
    /// (MLoop 응답은 확률을 class_0/class_1 키로 주고 PredictedLabel 은 별도라 직접 매핑이 없어
    /// max 를 쓴다 — 보수적이고 단조적인 선택.)
    /// </summary>
    private static double ConfidenceOf(PredictRow row)
    {
        if (row.Probabilities is { Count: > 0 } probs)
            return probs.Values.Max();
        return row.Score ?? 0.0;
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
    }
}
