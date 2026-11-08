using System.Text.Json;

namespace UVision.Edge.Worker;

/// <summary>The node-local camera alias to source map.</summary>
public static class CamerasFile
{
    /// <summary>Reads the file. Missing, unreadable or malformed is an empty map - a camera that cannot be resolved is reported lost, never skipped silently.</summary>
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } source)
                    map[p.Name] = source;
            return map;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}

/// <summary>
/// Keeps one <see cref="CameraRunner"/> going per alias in the cameras file and says "no signal" whenever a source is not there:
/// the alias is not (or no longer) in the file, a local file is missing, the source cannot be opened, or a recording has ended.
/// The file is looked at again every poll, so removing an alias or a recording stops that camera and putting it back starts it.
/// </summary>
public sealed class CameraSupervisor(
    WorkerOptions options, ILineSink sink, IDetector detector, IFrameSourceFactory frames, TimeProvider clock, Action<string> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var loops = new Dictionary<string, Task>(StringComparer.Ordinal);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // An alias that appears in the file gets a loop; a loop never ends on its own, it keeps reporting a missing alias as lost signal.
                foreach (var alias in CamerasFile.Read(options.CamerasFile).Keys)
                    if (!loops.ContainsKey(alias))
                        loops[alias] = Task.Run(() => CameraLoopAsync(alias, ct), CancellationToken.None);
                await Task.Delay(options.Poll, clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            try { await Task.WhenAll(loops.Values); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task PassesAsync(string alias, CameraRunner runner, Func<string?> getEnded, Action<string?> setEnded, CancellationToken ct)
    {
        string? endedKey = getEnded();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var source = CamerasFile.Read(options.CamerasFile).GetValueOrDefault(alias);
                if (source is null)
                {
                    endedKey = null; setEnded(null);
                    await runner.SetStateAsync(CameraState.NoSignal, ct);
                    await Task.Delay(options.Poll, clock, ct);
                    continue;
                }
                if (SourceKind.IsLocalFile(source) && !File.Exists(source))
                {
                    endedKey = null; setEnded(null);
                    await runner.SetStateAsync(CameraState.NoSignal, ct);
                    await Task.Delay(options.Poll, clock, ct);
                    continue;
                }

                var key = Key(source);
                if (endedKey == key)
                {
                    // The recording played to its end and has not changed since - lost signal until it is replaced.
                    await runner.SetStateAsync(CameraState.NoSignal, ct);
                    await Task.Delay(options.Poll, clock, ct);
                    continue;
                }

                IFrameSource opened;
                try { opened = frames.Open(source); }
                catch (FrameSourceException e)
                {
                    log($"camera={alias} cannot open the source: {e.Message}");
                    await runner.SetStateAsync(CameraState.NoSignal, ct);
                    await Task.Delay(options.Poll, clock, ct);
                    continue;
                }

                log($"camera={alias} source opened at {Wire.Iso(clock.GetUtcNow())}");
                using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var watcher = WatchAsync(alias, key, watch, ct);
                RunEnd end;
                try { end = await runner.RunAsync(opened, watch.Token); }
                catch (FrameSourceException e)
                {
                    log($"camera={alias} the source failed: {e.Message}");
                    await watch.CancelAsync();
                    await watcher;
                    await runner.SetStateAsync(CameraState.NoSignal, ct);
                    await Task.Delay(options.Poll, clock, ct);
                    continue;
                }

                await watch.CancelAsync();
                var changedUnderUs = await watcher;
                if (ct.IsCancellationRequested) break;
                if (changedUnderUs)
                {
                    // The alias or the file went away (or changed) while it played: stop, say so, and look again.
                    await runner.SetStateAsync(CameraState.NoSignal, ct);
                    continue;
                }
                if (end == RunEnd.Ended)
                {
                    if (options.Loop) continue;
                    endedKey = key; setEnded(key);
                    log($"camera={alias} the recording ended");
                    await runner.SetStateAsync(CameraState.NoSignal, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// Keeps a camera going whatever happens inside one pass. An unexpected failure is reported as lost signal and logged, then the camera is
    /// tried again - the alternative, a loop that ends without a word, would leave the last announced state (probably "ok") standing for a
    /// camera nobody is looking at.
    /// </summary>
    internal async Task CameraLoopAsync(string alias, CancellationToken ct)
    {
        var runner = new CameraRunner(alias, sink, detector, clock, log);
        var endedKey = (string?)null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PassesAsync(alias, runner, () => endedKey, k => endedKey = k, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                log($"camera={alias} unexpected failure, will retry: {e.GetType().Name}: {e.Message}");
                try
                {
                    await runner.SetStateAsync(CameraState.NoSignal, ct);
                    await Task.Delay(options.Poll, clock, ct);
                }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Checks the file every poll while a source plays and cancels the run if the alias vanished or the source changed. Returns true when it did.</summary>
    private async Task<bool> WatchAsync(string alias, string key, CancellationTokenSource run, CancellationToken ct)
    {
        try
        {
            while (!run.IsCancellationRequested)
            {
                await Task.Delay(options.Poll, clock, run.Token);
                var now = CamerasFile.Read(options.CamerasFile).GetValueOrDefault(alias);
                if (now is null || (SourceKind.IsLocalFile(now) && !File.Exists(now)) || Key(now) != key)
                {
                    log($"camera={alias} the source went away or changed");
                    await run.CancelAsync();
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { }
        return false;
    }

    /// <summary>Identifies "the same source": a stream address, or a local file's path with its size and write time (a replaced recording is a new source).</summary>
    private static string Key(string source)
    {
        if (!SourceKind.IsLocalFile(source)) return source;
        try
        {
            var info = new FileInfo(source);
            return $"{source}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException) { return source; }
    }
}
