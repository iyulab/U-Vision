using System.Diagnostics;

namespace UVision.Edge.Worker;

/// <summary>A camera source that could not be opened or died in a way that is not a normal end of the recording.</summary>
public sealed class FrameSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Produces frames from one camera source until it ends. Cancel the token to stop it early (the decoder is stopped with it).</summary>
public interface IFrameSource
{
    IAsyncEnumerable<Frame> ReadAsync(CancellationToken ct);
}

public interface IFrameSourceFactory
{
    /// <exception cref="FrameSourceException">The source cannot be opened.</exception>
    IFrameSource Open(string source);
}

/// <summary>Whether a source string names a local file (as opposed to a stream URL such as rtsp://).</summary>
public static class SourceKind
{
    public static bool IsLocalFile(string source) => !source.Contains("://", StringComparison.Ordinal);
}

/// <summary>
/// Decodes a source with the <c>ffmpeg</c> command-line tool run as a child process, sampling at a fixed rate and writing MJPEG to its stdout.
/// Nothing is linked: the decoder is a separate program. A local recording is read at its natural speed (<c>-re</c>) so playback behaves like a
/// camera; a stream URL already is live. Stopping the read stops the process.
/// </summary>
public sealed class FfmpegFrameSourceFactory(string ffmpegPath, double sampleFps, int maxWidth) : IFrameSourceFactory
{
    public IFrameSource Open(string source)
    {
        if (SourceKind.IsLocalFile(source) && !File.Exists(source))
            throw new FrameSourceException($"the file does not exist: {source}");
        return new FfmpegFrameSource(ffmpegPath, source, sampleFps, maxWidth);
    }
}

internal sealed class FfmpegFrameSource(string ffmpegPath, string source, double sampleFps, int maxWidth) : IFrameSource
{
    public async IAsyncEnumerable<Frame> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in Arguments()) info.ArgumentList.Add(a);

        Process process;
        try { process = Process.Start(info) ?? throw new FrameSourceException("the decoder did not start"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new FrameSourceException($"the decoder '{ffmpegPath}' cannot be started", e);
        }

        using var _ = process;
        // The decoder's messages are only kept for the failure case; draining them keeps its stderr pipe from filling and stalling it.
        var stderr = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null && stderr.Length < 4000) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        await using var stop = ct.Register(() => Kill(process));
        var frames = 0;
        await foreach (var frame in JpegFrames.ReadAsync(process.StandardOutput.BaseStream, ct))
        {
            frames++;
            yield return frame;
        }

        Kill(process);
        await process.WaitForExitAsync(CancellationToken.None);
        // No frame at all means the source never opened (missing stream, unreadable file) - that is a failure, not the end of a recording.
        if (frames == 0 && !ct.IsCancellationRequested)
            throw new FrameSourceException($"the source produced no frames: {Last(stderr.ToString())}");
    }

    private IEnumerable<string> Arguments()
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        if (SourceKind.IsLocalFile(source)) yield return "-re";
        yield return "-i";
        yield return source;
        yield return "-an";
        yield return "-vf";
        yield return FormattableString.Invariant($"fps={sampleFps},scale='min({maxWidth},iw)':-2");
        yield return "-f";
        yield return "image2pipe";
        yield return "-c:v";
        yield return "mjpeg";
        yield return "-q:v";
        yield return "4";
        yield return "pipe:1";
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
    }

    private static string Last(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no message";
}
