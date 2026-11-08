using System.Threading.Channels;

namespace UVision.Edge.Worker;

/// <summary>How one camera source run ended.</summary>
public enum RunEnd
{
    /// <summary>The recording ended normally (a camera never ends, a file does).</summary>
    Ended,

    /// <summary>The caller stopped it.</summary>
    Stopped,
}

/// <summary>
/// Looks at one camera: takes frames from a source, asks the detector for boxes, tracks them and writes the protocol lines. It never judges -
/// it reports what it sees and, honestly, when it cannot see.
/// <list type="bullet">
/// <item>A frame whose detection failed is <b>not</b> reported as "no objects" - that would be a made-up zero. After
/// <see cref="DegradedAfter"/> failures in a row the camera is reported <c>degraded</c>, and it returns to <c>ok</c> on the next good frame.</item>
/// <item>Frames are taken newest-first: if the detector is slower than the camera the older frames are dropped, so what the agent hears
/// stays close to the wall clock instead of lagging further behind.</item>
/// <item>The state the agent last heard is remembered; a non-ok state is said again when the agent reconnects, because a restarted
/// agent starts out believing every camera is ok.</item>
/// </list>
/// </summary>
public sealed class CameraRunner(string alias, ILineSink sink, IDetector detector, TimeProvider clock, Action<string>? log = null)
{
    public const int DegradedAfter = 3;

    private readonly IouTracker _tracker = new();
    private string _announced = CameraState.Ok;
    private int _announcedOnConnection = -1;
    private long _seq;
    private long _frames, _failed;
    private long _lastBeat;

    public string Alias => alias;

    /// <summary>The state this camera last reported to the agent.</summary>
    public string State => _announced;

    /// <summary>Reports a state (only when it changed, or again after a reconnect while it is not ok).</summary>
    public async ValueTask SetStateAsync(string state, CancellationToken ct)
    {
        var connections = sink.Connections;
        var changed = state != _announced;
        var reconnected = state != CameraState.Ok && connections != _announcedOnConnection;
        if (!changed && !reconnected) return;
        if (changed) log?.Invoke($"camera={alias} state={state}");
        _announced = state;
        if (await sink.SendAsync(Wire.SourceStateLine(alias, state, clock.GetUtcNow()), ct))
            _announcedOnConnection = sink.Connections;
    }

    /// <summary>A line every few seconds so a camera that stopped being served is visible in the log, not only in the console.</summary>
    private void Heartbeat()
    {
        var now = clock.GetTimestamp();
        if (_lastBeat != 0 && clock.GetElapsedTime(_lastBeat, now) < TimeSpan.FromSeconds(10)) return;
        _lastBeat = now;
        log?.Invoke($"camera={alias} frames={_frames} detection_failures={_failed} state={_announced}");
    }

    public async Task<RunEnd> RunAsync(IFrameSource source, CancellationToken ct)
    {
        // Newest frame wins: capacity one, oldest dropped.
        var latest = Channel.CreateBounded<Frame>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
        using var readerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var reading = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in source.ReadAsync(readerStop.Token))
                    latest.Writer.TryWrite(frame);
                latest.Writer.TryComplete();
            }
            catch (Exception e) { latest.Writer.TryComplete(e); }
        }, CancellationToken.None);

        var failures = 0;
        try
        {
            while (await latest.Reader.WaitToReadAsync(ct))
            {
                if (!latest.Reader.TryRead(out var frame)) continue;
                var time = clock.GetUtcNow();
                Heartbeat();
                _frames++;
                IReadOnlyList<Detection> detections;
                try
                {
                    detections = await detector.DetectAsync(frame, ct);
                }
                catch (DetectorException e)
                {
                    failures++;
                    _failed++;
                    log?.Invoke($"camera={alias} detection failed ({failures}): {e.Message}");
                    if (failures >= DegradedAfter) await SetStateAsync(CameraState.Degraded, ct);
                    continue;
                }

                failures = 0;
                await SetStateAsync(CameraState.Ok, ct);
                var tracks = _tracker.Update(detections);
                await sink.SendAsync(Wire.TracksLine(alias, _seq++, time, tracks), ct);
            }
            // The reader finished: a normal end, or the source failed (then the exception comes out here).
            await latest.Reader.Completion;
            return RunEnd.Ended;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RunEnd.Stopped;
        }
        finally
        {
            await readerStop.CancelAsync();
            await reading;
        }
    }
}
