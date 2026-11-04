using System.Runtime.CompilerServices;

namespace UVision.Edge.Agent.Worker;

/// <summary>
/// 줄 리더 결과 — <see cref="TooLong"/> 이면 한도를 넘은 줄을 통째로 버렸다는 표시다(<see cref="Bytes"/> 없음).
/// </summary>
public readonly record struct NdjsonLine(byte[]? Bytes)
{
    public bool TooLong => Bytes is null;
}

/// <summary>
/// 스트림을 줄(<c>\n</c>)로 나눈다 — 줄은 한도(<paramref name="maxLine"/>)를 넘으면 <b>끝까지 읽어 버리고</b> 다음 줄부터 이어 간다(워커의 깨진 줄 하나가
/// 연결을 끊거나 메모리를 불리지 않게). <c>\r</c> 은 줄 끝에서 떼고, 빈 줄은 건너뛰며, 줄바꿈 없이 끝난 마지막 토막(죽어 가는 워커가 쓰다 만 줄)은 버린다.
/// </summary>
public static class NdjsonLines
{
    public static async IAsyncEnumerable<NdjsonLine> ReadAsync(
        Stream stream, int maxLine, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[16 * 1024];
        using var line = new MemoryStream();
        var overflow = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                Append(buffer, start, i - start);
                var result = Finish();
                if (result is { } r) yield return r;
                start = i + 1;
            }
            Append(buffer, start, read - start);
        }

        void Append(byte[] bytes, int offset, int count)
        {
            if (overflow || count <= 0) return;
            if (line.Length + count > maxLine)
            {
                overflow = true;
                line.SetLength(0); // 버릴 줄의 앞부분을 붙들고 있지 않는다
                return;
            }
            line.Write(bytes, offset, count);
        }

        NdjsonLine? Finish()
        {
            NdjsonLine? result;
            if (overflow)
            {
                result = new NdjsonLine(null);
            }
            else
            {
                var bytes = line.ToArray();
                var length = bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
                result = length == 0 ? null : new NdjsonLine(length == bytes.Length ? bytes : bytes[..length]);
            }
            line.SetLength(0);
            overflow = false;
            return result;
        }
    }
}
