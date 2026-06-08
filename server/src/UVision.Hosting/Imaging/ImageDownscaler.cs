using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace UVision.Api.Imaging;

/// <summary>
/// 이미지를 longest-side <c>maxDim</c> px 로 축소하고 JPEG(q85) 재인코딩한다.
///
/// VLM vision 토큰 ≈ 픽셀수이므로 해상도 축소가 prefill latency 를 줄이는 레버다(M0.1 cycle-24~26 측정:
/// 256px 가 크로메이트/qwen3 에서 full-res recall 근접 + latency 1/5). sweet spot 은 모델/구성 의존이라
/// 하드코딩 불가 — 시나리오 <c>max_image_dimension</c> 으로 query+refs 에 대칭 적용한다.
///
/// 벤치마크(UVision.Benchmark)와 production(InspectEndpoints)이 이 단일 구현을 공유한다 —
/// 측정 변환 = production 변환(측정의 portability 보장).
/// </summary>
public static class ImageDownscaler
{
    private const int JpegQuality = 85;

    /// <summary>
    /// longest-side 가 <paramref name="maxDim"/> 초과 시 종횡비 유지 축소 후 JPEG 재인코딩한다.
    /// 이미 작아도 <paramref name="maxDim"/> &gt; 0 이면 JPEG 로 정규화한다(벤치마크와 동일 거동 →
    /// 측정 parity). <paramref name="maxDim"/> ≤ 0 은 no-op(원본 바이트 그대로 — 다운스케일 비활성).
    /// </summary>
    public static byte[] Downscale(ReadOnlyMemory<byte> data, int maxDim)
    {
        if (maxDim <= 0)
            return data.ToArray();

        using var image = Image.Load(data.Span);
        var longest = Math.Max(image.Width, image.Height);
        if (longest > maxDim)
        {
            var scale = (double)maxDim / longest;
            image.Mutate(x => x.Resize(
                (int)Math.Round(image.Width * scale), (int)Math.Round(image.Height * scale)));
        }
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQuality });
        return ms.ToArray();
    }
}
