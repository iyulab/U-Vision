using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using UVision.Api.Configuration;

namespace UVision.Api.Services.Memory;

/// <summary>
/// MLoop <c>mloop serve</c> 의 <c>POST /embed</c> 를 쓰는 임베더 — 프로세스 경계로만 소비한다(전용 ML 분류기와 같은 꼴).
/// 이미지 <b>내용</b>을 base64 로 보내므로 MLoop 이 같은 호스트에 있을 필요가 없다.
/// <para>
/// 검증 범위: MLoop 0.44.0 소스에서 확인한 계약을 fake-handler 계약 테스트가 가드한다. 실 인코더와의 왕복은
/// 아직 하지 않았다(UNVERIFIED — 설치 쪽 인코더가 정해진 뒤).
/// </para>
/// </summary>
public sealed class MloopImageEmbedder(HttpClient http, MemoryOptions options) : IImageEmbedder
{
    public string Name => "mloop";

    public bool IsEnabled => true;

    public async Task<ImageEmbeddings> EmbedAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> images, CancellationToken cancellationToken = default)
    {
        if (images.Count is 0 or > IImageEmbedder.MaxBatch)
            throw new ArgumentOutOfRangeException(nameof(images), images.Count, $"1~{IImageEmbedder.MaxBatch} 장");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"embed?encoder={Uri.EscapeDataString(options.Encoder)}")
        {
            Content = JsonContent.Create(new EmbedRequest([.. images.Select(i => Convert.ToBase64String(i.Span))])),
        };
        if (!string.IsNullOrEmpty(options.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 400(몇 번째 장을 못 읽음) · 409(인코더 · 런타임 미설치 — 본문에 설치 방법) · 413 — 본문을 그대로 사유로.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"mloop /embed 실패 ({(int)response.StatusCode}): {body}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken)
            ?? throw new InvalidOperationException("mloop /embed 응답이 비었다");
        var encoder = parsed.Encoder ?? throw new InvalidOperationException("mloop /embed 응답에 encoder 가 없다");
        var vectors = parsed.Vectors ?? [];
        if (vectors.Count != images.Count)
            throw new InvalidOperationException($"mloop /embed 가 {images.Count} 장에 {vectors.Count} 개 벡터를 냈다");
        if (vectors.Any(v => v.Length != encoder.Dimension || v.Any(x => !float.IsFinite(x))))
            throw new InvalidOperationException("mloop /embed 벡터의 차원이 encoder.dimension 과 다르거나 유한하지 않다");

        // 경계에서 정규화를 보장한다 — 거리 계산(코사인 = 내적)이 그것을 전제한다.
        return new ImageEmbeddings(
            new EncoderIdentity(encoder.Id, encoder.Version, encoder.Sha256, encoder.Dimension, Normalized: true),
            encoder.Normalized ? vectors : [.. vectors.Select(VectorMath.Normalize)]);
    }

    // --- mloop /embed 계약(camelCase, MLoop 0.44.0 소스 확인) ------------------

    private sealed record EmbedRequest([property: JsonPropertyName("images")] IReadOnlyList<string> Images);

    private sealed record EmbedResponse
    {
        [JsonPropertyName("encoder")] public EncoderDto? Encoder { get; init; }
        [JsonPropertyName("vectors")] public List<float[]>? Vectors { get; init; }
    }

    private sealed record EncoderDto
    {
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("version")] public required string Version { get; init; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
        [JsonPropertyName("dimension")] public int Dimension { get; init; }
        [JsonPropertyName("normalized")] public bool Normalized { get; init; }
    }
}
