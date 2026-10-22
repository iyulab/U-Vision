namespace UVision.Api.Imaging;

/// <summary>
/// 분리형 바이큐빅(Catmull-Rom) 리샘플러 — RGBA8888 픽셀 버퍼를 받아 RGBA8888 을 돌려주는 순수 함수.
///
/// 왜 직접 짓는가: 다운스케일 사본은 VLM 입력이라 커널이 바뀌면 판정이 조용히 달라진다. 이전 구현(ImageSharp
/// 기본 Resize)은 축소 시 커널 폭을 축소 비율만큼 넓혀 고주파를 평균냈다. Skia 의 샘플링 옵션은 이렇게
/// 커널을 넓히지 않는다. 그래서 같은 방식으로 넓히는 리샘플러를 두고, 이미지 라이브러리는 디코드·인코드만
/// 맡긴다. 이전 구현과의 일치는 보존 테스트 `Resample_MatchesPreviousImplementation`이 지킨다.
/// </summary>
public static class BicubicResampler
{
    private const int Channels = 4;

    /// <summary>
    /// <paramref name="src"/>(행 우선 RGBA8888, 길이 = w·h·4)를 <paramref name="dstWidth"/>×<paramref name="dstHeight"/> 로 리샘플한다.
    /// </summary>
    public static byte[] Resize(ReadOnlySpan<byte> src, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "Image dimensions must be positive.");
        if (src.Length != (long)srcWidth * srcHeight * Channels)
            throw new ArgumentException("Pixel buffer length does not match the dimensions.", nameof(src));

        var xWeights = Weights(srcWidth, dstWidth);
        var yWeights = Weights(srcHeight, dstHeight);

        // 가로 먼저: srcHeight × dstWidth 의 float 중간 버퍼(반올림 오차를 두 번 쌓지 않는다).
        var horizontal = new float[(long)srcHeight * dstWidth * Channels];
        for (int y = 0; y < srcHeight; y++)
        {
            var row = src.Slice(y * srcWidth * Channels, srcWidth * Channels);
            for (int x = 0; x < dstWidth; x++)
            {
                var (start, w) = xWeights[x];
                float r = 0, g = 0, b = 0, a = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    int i = (start + k) * Channels;
                    r += row[i] * w[k];
                    g += row[i + 1] * w[k];
                    b += row[i + 2] * w[k];
                    a += row[i + 3] * w[k];
                }
                long o = ((long)y * dstWidth + x) * Channels;
                horizontal[o] = r;
                horizontal[o + 1] = g;
                horizontal[o + 2] = b;
                horizontal[o + 3] = a;
            }
        }

        var dst = new byte[(long)dstWidth * dstHeight * Channels];
        for (int y = 0; y < dstHeight; y++)
        {
            var (start, w) = yWeights[y];
            for (int x = 0; x < dstWidth; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    long i = ((long)(start + k) * dstWidth + x) * Channels;
                    r += horizontal[i] * w[k];
                    g += horizontal[i + 1] * w[k];
                    b += horizontal[i + 2] * w[k];
                    a += horizontal[i + 3] * w[k];
                }
                long o = ((long)y * dstWidth + x) * Channels;
                dst[o] = ToByte(r);
                dst[o + 1] = ToByte(g);
                dst[o + 2] = ToByte(b);
                dst[o + 3] = ToByte(a);
            }
        }
        return dst;
    }

    /// <summary>
    /// 목적 좌표마다 (첫 소스 인덱스, 정규화된 가중치들). 축소 시 커널 반경을 비율만큼 넓히고, 가장자리는
    /// 소스 범위 안으로 잘라 남은 가중치로 다시 정규화한다.
    /// </summary>
    private static (int Start, float[] Weights)[] Weights(int srcSize, int dstSize)
    {
        double ratio = (double)srcSize / dstSize;
        double scale = Math.Max(ratio, 1.0);
        double radius = 2.0 * scale; // Catmull-Rom 지지 반경 2
        var result = new (int, float[])[dstSize];
        for (int d = 0; d < dstSize; d++)
        {
            double center = (d + 0.5) * ratio - 0.5;
            int left = Math.Max(0, (int)Math.Ceiling(center - radius));
            int right = Math.Min(srcSize - 1, (int)Math.Floor(center + radius));
            var w = new float[right - left + 1];
            double sum = 0;
            for (int s = left; s <= right; s++)
            {
                double v = CatmullRom((s - center) / scale);
                w[s - left] = (float)v;
                sum += v;
            }
            if (sum != 0)
                for (int k = 0; k < w.Length; k++)
                    w[k] = (float)(w[k] / sum);
            result[d] = (left, w);
        }
        return result;
    }

    private static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
        if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
        return 0;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
