using SkiaSharp;

namespace UVision.Tests;

/// <summary>테스트 이미지 픽스처 — 프로덕션과 같은 이미지 라이브러리(SkiaSharp)로 만들고 읽는다.</summary>
internal static class TestImages
{
    /// <summary>지정 크기 단색 JPEG 바이트.</summary>
    public static byte[] MakeJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Black);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 75);
        return data.ToArray();
    }

    public static (int W, int H) Dimensions(ReadOnlySpan<byte> encoded)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(encoded.ToArray()))
            ?? throw new InvalidDataException("decodable image expected");
        return (codec.Info.Width, codec.Info.Height);
    }

    public static SKBitmap DecodeRgb(byte[] encoded) =>
        SKBitmap.Decode(encoded)?.Copy(SKColorType.Rgba8888)
            ?? throw new InvalidDataException("decodable image expected");
}
