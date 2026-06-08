using SixLabors.ImageSharp;
using UVision.Api.Imaging;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 서버사이드 다운스케일 레버(<see cref="ImageDownscaler"/>) 단위 검증 — 키 불필요.
/// query+refs 에 대칭 적용되는 변환이므로 거동 불변식을 직접 못박는다.
/// </summary>
public class ImageDownscalerTests
{
    /// <summary>지정 크기 단색 JPEG 바이트 생성(테스트 픽스처).</summary>
    private static byte[] MakeJpeg(int width, int height)
    {
        using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private static (int W, int H) DimensionsOf(byte[] data)
    {
        using var image = Image.Load(data);
        return (image.Width, image.Height);
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
