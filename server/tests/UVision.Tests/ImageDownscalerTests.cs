using UVision.Api.Imaging;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 서버사이드 다운스케일 레버(<see cref="ImageDownscaler"/>) 단위 검증 — 키 불필요.
/// query+refs 에 대칭 적용되는 변환이므로 거동 불변식을 직접 못박는다.
/// </summary>
public class ImageDownscalerTests
{
    private static byte[] MakeJpeg(int width, int height) => TestImages.MakeJpeg(width, height);

    private static (int W, int H) DimensionsOf(byte[] data) => TestImages.Dimensions(data);

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Downscale", name);

    /// <summary>
    /// 보존 테스트 — 이미지 라이브러리 교체(ImageSharp 3.1.12 → SkiaSharp + <see cref="BicubicResampler"/>) 전에
    /// 옛 구현의 리샘플링(ImageSharp 기본 Resize)으로 만든 기준 픽셀과 비교한다. 다운스케일 사본은 VLM 입력이라
    /// 커널이 바뀌면 판정이 조용히 달라질 수 있다. 소스는 체커·사선 고주파를 담아 큰 축소의 커널 차이를 드러낸다.
    /// 비교는 JPEG 인코딩 전 픽셀로 한다 — 이 합성 소스는 JPEG 한 번에 채널 평균 ~20 레벨을 잃어(크로마
    /// 서브샘플링) 인코딩 후 비교로는 커널 차이를 가려낼 수 없다.
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(160)]
    public void Resample_MatchesPreviousImplementation(int maxDim)
    {
        using var source = TestImages.DecodeRgb(File.ReadAllBytes(Fixture("downscale-source.png")));
        using var reference = TestImages.DecodeRgb(File.ReadAllBytes(Fixture($"downscale-ref-{maxDim}.png")));

        var actual = BicubicResampler.Resize(
            source.GetPixelSpan(), source.Width, source.Height, reference.Width, reference.Height);

        var expected = reference.GetPixelSpan();
        double sum = 0;
        int max = 0;
        for (int i = 0; i < actual.Length; i += 4)
            for (int c = 0; c < 3; c++)
            {
                int d = Math.Abs(actual[i + c] - expected[i + c]);
                sum += d;
                max = Math.Max(max, d);
            }
        var mean = sum / (actual.Length / 4 * 3.0);
        Assert.True(mean <= 1.0, $"mean abs diff per channel {mean:F3} > 1.0 (max {max})");
    }

    [Fact]
    public void Downscale_ProducesJpeg()
    {
        var result = ImageDownscaler.Downscale(TestImages.MakeJpeg(800, 400), 256);

        Assert.True(result.Length > 2 && result[0] == 0xFF && result[1] == 0xD8, "JPEG SOI marker expected");
    }

    [Fact]
    public void Downscale_LongestSideExceedsMax_ShrinksKeepingAspect()
    {
        var src = MakeJpeg(800, 400); // longest=800

        var (w, h) = DimensionsOf(ImageDownscaler.Downscale(src, 256));

        Assert.Equal(256, w);     // longest → maxDim
        Assert.Equal(128, h);     // 종횡비(2:1) 보존
    }

    [Fact]
    public void Downscale_AlreadySmaller_KeepsDimensions()
    {
        var src = MakeJpeg(200, 150); // longest=200 < 256

        var (w, h) = DimensionsOf(ImageDownscaler.Downscale(src, 256));

        Assert.Equal(200, w);     // 확대하지 않음
        Assert.Equal(150, h);
    }

    [Fact]
    public void Downscale_MaxDimZero_ReturnsOriginalBytes()
    {
        var src = MakeJpeg(800, 400);

        var result = ImageDownscaler.Downscale(src, 0);

        Assert.Equal(src, result); // no-op: 원본 바이트 그대로(다운스케일 비활성)
    }

    [Fact]
    public void Downscale_MaxDimNegative_ReturnsOriginalBytes()
    {
        var src = MakeJpeg(800, 400);

        var result = ImageDownscaler.Downscale(src, -1);

        Assert.Equal(src, result);
    }
}
