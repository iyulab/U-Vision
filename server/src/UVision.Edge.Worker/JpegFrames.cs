namespace UVision.Edge.Worker;

/// <summary>One decoded camera frame as a JPEG with its pixel size (read from the JPEG header, so the detector's pixel boxes can be normalised).</summary>
public sealed record Frame(byte[] Jpeg, int Width, int Height);

/// <summary>
/// Splits an <c>image2pipe</c> MJPEG byte stream into single JPEG images. Inside entropy-coded JPEG data a 0xFF byte is always followed by
/// 0x00 (stuffing) or a restart marker, so the end-of-image marker 0xFF 0xD9 only appears at the end of a frame - the encoder in use writes no
/// embedded thumbnails.
/// </summary>
public static class JpegFrames
{
    /// <summary>Largest single frame accepted - a runaway stream must not grow the buffer without bound.</summary>
    public const int MaxFrameBytes = 32 * 1024 * 1024;

    public static async IAsyncEnumerable<Frame> ReadAsync(
        Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[256 * 1024];
        var held = new List<byte>(buffer.Length);
        var scanFrom = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) yield break;
            held.AddRange(buffer.AsSpan(0, read).ToArray());
            if (held.Count > MaxFrameBytes) throw new InvalidDataException("frame larger than the limit - not an MJPEG stream");

            while (true)
            {
                var start = IndexOf(held, 0xFF, 0xD8, 0);
                if (start < 0)
                {
                    // No start-of-image yet. A lone trailing 0xFF may be the first half of one that the next read completes - keep it.
                    var keepLast = held.Count > 0 && held[^1] == 0xFF;
                    held.Clear();
                    if (keepLast) held.Add(0xFF);
                    scanFrom = 0;
                    break;
                }
                if (start > 0) { held.RemoveRange(0, start); scanFrom = 0; }
                var end = IndexOf(held, 0xFF, 0xD9, Math.Max(2, scanFrom));
                if (end < 0) { scanFrom = Math.Max(0, held.Count - 1); break; }
                var jpeg = held.GetRange(0, end + 2).ToArray();
                held.RemoveRange(0, end + 2);
                scanFrom = 0;
                if (TrySize(jpeg, out var w, out var h)) yield return new Frame(jpeg, w, h);
            }
        }
    }

    private static int IndexOf(List<byte> list, byte a, byte b, int from)
    {
        for (var i = from; i < list.Count - 1; i++)
            if (list[i] == a && list[i + 1] == b) return i;
        return -1;
    }

    /// <summary>Reads the pixel size from the start-of-frame segment. False when the bytes are not a JPEG with a readable header.</summary>
    public static bool TrySize(byte[] jpeg, out int width, out int height)
    {
        width = height = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8)) { i += 2; continue; }
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC) carry the frame size.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return width > 0 && height > 0;
            }
            i += 2 + length;
        }
        return false;
    }
}
