using System.Net;
using System.Text.Json;
using UVision.Edge.Agent.Rules;
using UVision.Edge.Agent.Worker;
using UVision.Edge.Worker;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// The software inference worker: the lines it writes are read back by the agent's own parser, the detector's pixel boxes are normalised, the
/// tracker keeps identity, and a camera that cannot be seen is reported lost - never silently dropped and never as "zero objects".
/// </summary>
public class EdgeWorkerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    // --- the line protocol, read back by the agent's real parser -------------------------------------------------

    [Fact]
    public void TracksLine_IsReadBackByTheAgentsParser()
    {
        var line = Wire.TracksLine("dock-1", 42, T0.AddMilliseconds(123), [new Track("7", "box", 0.91, new Box(0.12, 0.30, 0.10, 0.18))]);

        Assert.EndsWith("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.True(WorkerProtocol.TryParse(System.Text.Encoding.UTF8.GetBytes(line.TrimEnd('\n')), out var message, out var reason), reason);
        var frame = Assert.IsType<TrackFrame>(message);
        Assert.Equal("dock-1", frame.CameraRef);
        Assert.Equal(42, frame.Seq);
        Assert.Equal(T0.AddMilliseconds(123), frame.Time);
        var track = Assert.Single(frame.Tracks);
        Assert.Equal(("7", "box"), (track.TrackId, track.Class));
        Assert.Equal(0.12, track.X, 5);
        Assert.Equal(0.18, track.H, 5);
    }

    [Fact]
    public void AFrameWithNoObjects_IsAFrame_NotAbsence()
    {
        var line = Wire.TracksLine("dock-1", 1, T0, []);

        Assert.True(WorkerProtocol.TryParse(System.Text.Encoding.UTF8.GetBytes(line.TrimEnd('\n')), out var message, out _));
        Assert.Empty(Assert.IsType<TrackFrame>(message).Tracks);
    }

    [Theory]
    [InlineData(CameraState.Ok)]
    [InlineData(CameraState.NoSignal)]
    [InlineData(CameraState.Degraded)]
    public void SourceStateLine_IsReadBackByTheAgentsParser(string state)
    {
        var line = Wire.SourceStateLine("dock-1", state, T0);

        Assert.True(WorkerProtocol.TryParse(System.Text.Encoding.UTF8.GetBytes(line.TrimEnd('\n')), out var message, out var reason), reason);
        var s = Assert.IsType<SourceStateMessage>(message);
        Assert.Equal(("dock-1", state), (s.CameraRef, s.State));
    }

    [Fact]
    public void TimesAreAlwaysUtcWithAZone_WhateverTheInputOffset()
    {
        Assert.Equal("2026-11-08T03:00:00.000Z", Wire.Iso(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.FromHours(9))));
    }

    // --- JPEG framing ---------------------------------------------------------------------------------------------

    /// <summary>A minimal JPEG skeleton: SOI, a baseline start-of-frame with the size, a stuffed scan, EOI.</summary>
    private static byte[] Jpeg(int width, int height, byte fill = 0x11)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00 };
        bytes.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, fill, 0xFF, 0x00, fill]);   // 0xFF 0x00 is a stuffed data byte
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    private static async Task<List<Frame>> ReadAllAsync(Stream s)
    {
        var frames = new List<Frame>();
        await foreach (var f in JpegFrames.ReadAsync(s)) frames.Add(f);
        return frames;
    }

    private sealed class Trickle(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(chunk, count), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _pos);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100000)]
    public async Task JpegFrames_SplitsAStreamIntoFrames_WhateverTheChunking(int chunk)
    {
        var stream = new List<byte>();
        stream.AddRange(Jpeg(640, 480, 0x22));
        stream.AddRange(Jpeg(320, 200, 0x33));
        stream.AddRange(Jpeg(640, 480, 0x44));

        var frames = await ReadAllAsync(new Trickle([.. stream], chunk));

        Assert.Equal([(640, 480), (320, 200), (640, 480)], frames.Select(f => (f.Width, f.Height)));
        Assert.All(frames, f => Assert.Equal([0xFF, 0xD9], f.Jpeg[^2..]));
    }

    [Fact]
    public async Task JpegFrames_IgnoresLeadingGarbage_AndDropsATruncatedLastFrame()
    {
        var stream = new List<byte> { 1, 2, 3, 0xFF, 0x00 };
        stream.AddRange(Jpeg(64, 48));
        stream.AddRange(Jpeg(64, 48)[..^4]);   // the decoder died mid-frame

        var frames = await ReadAllAsync(new Trickle([.. stream], 5));

        Assert.Single(frames);
    }

    [Fact]
    public void JpegFrames_TrySize_RejectsWhatIsNotAJpeg()
    {
        Assert.False(JpegFrames.TrySize([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], out _, out _));
        Assert.False(JpegFrames.TrySize([0xFF, 0xD8, 0xFF, 0xD9], out _, out _));
    }

    // --- the detector --------------------------------------------------------------------------------------------

    private static JsonElement Answer(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Detector_NormalisesPixelBoxes_AndKeepsOnlyWhatPassesTheFloorAndTheClassFilter()
    {
        var answer = Answer("""
            {"predictions":[{"detections":[
              {"label":"helmet","score":0.9,"x0":100,"y0":50,"x1":200,"y1":150},
              {"label":"helmet","score":0.2,"x0":10,"y0":10,"x1":20,"y1":20},
              {"label":"head","score":0.9,"x0":10,"y0":10,"x1":20,"y1":20},
              {"label":"helmet","score":0.8,"x0":-30,"y0":-10,"x1":50,"y1":40}
            ]}]}
            """);

        var found = HttpDetector.Parse(answer, 400, 200, 0.3, new HashSet<string> { "helmet" });

        Assert.Equal(2, found.Count);
        Assert.Equal(new Box(0.25, 0.25, 0.25, 0.5), found[0].Box);
        Assert.Equal(0.0, found[1].Box.X);   // clamped into the frame
        Assert.Equal(0.0, found[1].Box.Y);
        Assert.Equal(50.0 / 400, found[1].Box.W, 6);
    }

    [Fact]
    public void Detector_SkipsMalformedBoxes_ButRejectsAnAnswerWithoutPredictions()
    {
        var odd = Answer("""{"predictions":[{"detections":[{"label":"helmet","score":0.9,"x0":5,"y0":5,"x1":5,"y1":9},{"label":"helmet"},"x"]}]}""");
        Assert.Empty(HttpDetector.Parse(odd, 100, 100, 0.1, null));

        Assert.Throws<DetectorException>(() => HttpDetector.Parse(Answer("""{"error":"no model"}"""), 100, 100, 0.1, null));
        Assert.Throws<DetectorException>(() => HttpDetector.Parse(Answer("""{"predictions":[]}"""), 100, 100, 0.1, null));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    [Fact]
    public async Task Detector_SendsTheScoreFloorInTheBody_AndReportsFailuresAsDetectorErrors()
    {
        string? sentBody = null;
        var ok = new HttpClient(new Handler(async req =>
        {
            sentBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"predictions":[{"detections":[]}]}""") };
        })) { BaseAddress = new Uri("http://model/") };
        var frame = new Frame([1, 2, 3], 100, 100);

        Assert.Empty(await new HttpDetector(ok, 0.05, null).DetectAsync(frame, CancellationToken.None));
        using var body = JsonDocument.Parse(sentBody!);
        Assert.Equal(0.05, body.RootElement.GetProperty("scoreThreshold").GetDouble());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), body.RootElement.GetProperty("images")[0].GetString());

        var failing = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)))) { BaseAddress = new Uri("http://model/") };
        await Assert.ThrowsAsync<DetectorException>(() => new HttpDetector(failing, 0.05, null).DetectAsync(frame, CancellationToken.None));

        var down = new HttpClient(new Handler(_ => throw new HttpRequestException("refused"))) { BaseAddress = new Uri("http://model/") };
        await Assert.ThrowsAsync<DetectorException>(() => new HttpDetector(down, 0.05, null).DetectAsync(frame, CancellationToken.None));
    }

    // --- the tracker ---------------------------------------------------------------------------------------------

    private static Detection D(string label, double x, double y, double w = 0.2, double h = 0.2) => new(label, 0.9, new Box(x, y, w, h));

    [Fact]
    public void Tracker_KeepsAnIdWhileTheObjectMoves_AndGivesNewObjectsNewIds()
    {
        var tracker = new IouTracker();

        var first = tracker.Update([D("box", 0.10, 0.10), D("box", 0.60, 0.60)]);
        var second = tracker.Update([D("box", 0.12, 0.11), D("box", 0.61, 0.60), D("box", 0.30, 0.80)]);

        Assert.Equal(first.Select(t => t.Id), second.Take(2).Select(t => t.Id));
        Assert.Equal(3, second.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Tracker_ReportsOnlyWhatIsSeenNow_AndKeepsTheIdThroughAShortGap()
    {
        var tracker = new IouTracker();
        var id = tracker.Update([D("box", 0.1, 0.1)]).Single().Id;

        Assert.Empty(tracker.Update([]));
        Assert.Equal(id, tracker.Update([D("box", 0.1, 0.1)]).Single().Id);
    }

    [Fact]
    public void Tracker_ForgetsAfterTooManyMissedFrames_AndNeverMatchesAcrossClasses()
    {
        var tracker = new IouTracker(maxMissedFrames: 1);
        var id = tracker.Update([D("box", 0.1, 0.1)]).Single().Id;
        tracker.Update([]);
        tracker.Update([]);

        Assert.NotEqual(id, tracker.Update([D("box", 0.1, 0.1)]).Single().Id);

        var other = new IouTracker();
        var boxId = other.Update([D("box", 0.1, 0.1)]).Single().Id;
        Assert.NotEqual(boxId, other.Update([D("tray", 0.1, 0.1)]).Single().Id);
    }

    // --- options -------------------------------------------------------------------------------------------------

    private static Dictionary<string, string?> Env(params (string, string)[] extra)
    {
        var env = new Dictionary<string, string?>
        {
            ["UVISION_WORKER_SOCKET"] = "/sock/w.sock", ["UVISION_CAMERAS_FILE"] = "/cfg/cameras.json", ["UVISION_DETECTOR_URL"] = "http://model:5000",
        };
        foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    [Fact]
    public void Options_HaveDefaults_AndRejectEveryProblemAtOnce()
    {
        var ok = WorkerOptions.FromEnvironment(Env(("UVISION_CLASSES", "helmet, head")));
        Assert.Equal(2, ok.SampleFps);
        Assert.False(ok.Loop);
        Assert.Equal(["head", "helmet"], ok.Classes.Order());

        var e = Assert.Throws<WorkerConfigException>(() => WorkerOptions.FromEnvironment(new Dictionary<string, string?>
        {
            ["UVISION_WORKER_SOCKET"] = "relative.sock", ["UVISION_DETECTOR_URL"] = "ftp://x", ["UVISION_SAMPLE_FPS"] = "0", ["UVISION_VIDEO_LOOP"] = "maybe",
        }));
        Assert.Contains(e.Problems, p => p.Contains("UVISION_WORKER_SOCKET must be an absolute path"));
        Assert.Contains(e.Problems, p => p.Contains("UVISION_CAMERAS_FILE is required"));
        Assert.Contains(e.Problems, p => p.Contains("UVISION_DETECTOR_URL must be an http(s)"));
        Assert.Contains(e.Problems, p => p.Contains("UVISION_SAMPLE_FPS"));
        Assert.Contains(e.Problems, p => p.Contains("UVISION_VIDEO_LOOP"));
    }

    // --- the camera runner and supervisor -----------------------------------------------------------------------

    private sealed class Sink : ILineSink
    {
        public List<string> Lines { get; } = [];
        public int Connections { get; set; } = 1;
        public bool Up { get; set; } = true;
        public ValueTask<bool> SendAsync(string line, CancellationToken ct)
        {
            if (!Up) return ValueTask.FromResult(false);
            lock (Lines) Lines.Add(line);
            return ValueTask.FromResult(true);
        }
        public string[] Snapshot() { lock (Lines) return [.. Lines]; }
        public string[] States() => [.. Snapshot().Where(l => l.Contains("source_state")).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("state").GetString()!)];
        public int TrackFrames() => Snapshot().Count(l => l.Contains("\"tracks\""));
    }

    private sealed class ScriptedDetector(Func<int, IReadOnlyList<Detection>> answer) : IDetector
    {
        private int _calls;
        public int Calls => _calls;
        public Task<IReadOnlyList<Detection>> DetectAsync(Frame frame, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            return Task.FromResult(answer(n));
        }
    }

    private sealed class FailingDetector(int failFrom, int failUntil) : IDetector
    {
        private int _calls;
        public Task<IReadOnlyList<Detection>> DetectAsync(Frame frame, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n >= failFrom && n <= failUntil) throw new DetectorException("down");
            return Task.FromResult<IReadOnlyList<Detection>>([D("box", 0.1, 0.1)]);
        }
    }

    private sealed class Frames(int count, TimeSpan every, bool endless = false, Exception? failAfter = null) : IFrameSource
    {
        public async IAsyncEnumerable<Frame> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < count; i++)
            {
                await Task.Delay(every, ct);
                yield return new Frame([1], 100, 100);
            }
            if (failAfter is not null) throw failAfter;
            if (endless) await Task.Delay(Timeout.Infinite, ct);
        }
    }

    private sealed class Factory(Func<string, IFrameSource> open) : IFrameSourceFactory
    {
        public IFrameSource Open(string source) => open(source);
    }

    private static async Task<bool> Eventually(Func<bool> condition, int millis = 5000)
    {
        var until = Environment.TickCount64 + millis;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task Runner_ReportsEveryFrame_IncludingAnEmptyOne_AndEndsWhenTheSourceEnds()
    {
        var sink = new Sink();
        var detector = new ScriptedDetector(n => n == 2 ? [] : [D("box", 0.1, 0.1)]);
        var runner = new CameraRunner("dock-1", sink, detector, TimeProvider.System);

        var end = await runner.RunAsync(new Frames(3, TimeSpan.FromMilliseconds(30)), CancellationToken.None);

        Assert.Equal(RunEnd.Ended, end);
        Assert.Equal(3, sink.TrackFrames());
        Assert.Empty(sink.States());   // ok is the starting state - nothing to announce
        var counts = sink.Snapshot().Select(l => JsonDocument.Parse(l).RootElement.GetProperty("tracks").GetArrayLength()).ToArray();
        Assert.Equal([1, 0, 1], counts);
    }

    [Fact]
    public async Task Runner_DoesNotReportZeroObjectsWhenDetectionFailed_AndTurnsDegradedThenBackToOk()
    {
        var sink = new Sink();
        var runner = new CameraRunner("dock-1", sink, new FailingDetector(failFrom: 2, failUntil: 4), TimeProvider.System);

        await runner.RunAsync(new Frames(6, TimeSpan.FromMilliseconds(40)), CancellationToken.None);

        // Frame 1 ok, frames 2-4 failed (no line for them - a made-up zero would pass for "nothing there"), frames 5-6 ok again.
        Assert.Equal(3, sink.TrackFrames());
        Assert.Equal([CameraState.Degraded, CameraState.Ok], sink.States());
    }

    [Fact]
    public async Task Runner_SaysANonOkStateAgainWhenTheAgentReconnects()
    {
        var sink = new Sink();
        var runner = new CameraRunner("dock-1", sink, new ScriptedDetector(_ => []), TimeProvider.System);

        await runner.SetStateAsync(CameraState.NoSignal, CancellationToken.None);
        await runner.SetStateAsync(CameraState.NoSignal, CancellationToken.None);   // nothing new
        Assert.Equal([CameraState.NoSignal], sink.States());

        sink.Connections++;   // a restarted agent believes every camera is ok
        await runner.SetStateAsync(CameraState.NoSignal, CancellationToken.None);
        Assert.Equal([CameraState.NoSignal, CameraState.NoSignal], sink.States());
    }

    [Fact]
    public async Task Runner_KeepsTheStateToSayAgain_WhenTheAgentWasNotThereForTheFirstTelling()
    {
        var sink = new Sink { Up = false };
        var runner = new CameraRunner("dock-1", sink, new ScriptedDetector(_ => []), TimeProvider.System);
        await runner.SetStateAsync(CameraState.NoSignal, CancellationToken.None);   // dropped: nobody listening
        Assert.Empty(sink.States());

        sink.Up = true;
        sink.Connections++;
        await runner.SetStateAsync(CameraState.NoSignal, CancellationToken.None);

        Assert.Equal([CameraState.NoSignal], sink.States());
    }

    [Fact]
    public async Task Runner_DropsOldFrames_WhenTheDetectorIsSlowerThanTheCamera()
    {
        var sink = new Sink();
        var slow = new SlowDetector(TimeSpan.FromMilliseconds(120));
        var runner = new CameraRunner("dock-1", sink, slow, TimeProvider.System);

        await runner.RunAsync(new Frames(20, TimeSpan.FromMilliseconds(10)), CancellationToken.None);

        Assert.InRange(slow.Calls, 2, 10);   // far fewer than the 20 frames the camera produced - it did not queue them
    }

    private sealed class SlowDetector(TimeSpan delay) : IDetector
    {
        private int _calls;
        public int Calls => _calls;
        public async Task<IReadOnlyList<Detection>> DetectAsync(Frame frame, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(delay, ct);
            return [];
        }
    }

    [Fact]
    public async Task Runner_ASourceFailureComesOutOfRun()
    {
        var runner = new CameraRunner("dock-1", new Sink(), new ScriptedDetector(_ => []), TimeProvider.System);

        await Assert.ThrowsAsync<FrameSourceException>(() =>
            runner.RunAsync(new Frames(1, TimeSpan.FromMilliseconds(10), failAfter: new FrameSourceException("decoder died")), CancellationToken.None));
    }

    private sealed class SupervisorRig : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "uv-worker-" + Guid.NewGuid().ToString("N"));
        public string Cameras => Path.Combine(Dir, "cameras.json");
        public Sink Sink { get; } = new();
        public CancellationTokenSource Stop { get; } = new();
        public Task? Running { get; private set; }
        private readonly List<string> _log = [];

        public SupervisorRig()
        {
            Directory.CreateDirectory(Dir);
        }

        public string Video(string name)
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path, "not really a video");
            return path;
        }

        public void SetCameras(params (string Alias, string Source)[] map) =>
            File.WriteAllText(Cameras, JsonSerializer.Serialize(map.ToDictionary(m => m.Alias, m => m.Source)));

        public void Start(Func<string, IFrameSource> open, bool loop = false)
        {
            var options = new WorkerOptions
            {
                SocketPath = "/unused", CamerasFile = Cameras, DetectorUrl = new Uri("http://model/"), Poll = TimeSpan.FromMilliseconds(40), Loop = loop,
            };
            var supervisor = new CameraSupervisor(options, Sink, new ScriptedDetector(_ => [D("box", 0.1, 0.1)]), new Factory(open), TimeProvider.System, s => { lock (_log) _log.Add(s); });
            Running = Task.Run(() => supervisor.RunAsync(Stop.Token));
        }

        public void Dispose()
        {
            Stop.Cancel();
            try { Running?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Supervisor_AnAliasThatIsNotInTheFile_IsLostSignal_AndStartsWhenItAppears()
    {
        using var rig = new SupervisorRig();
        rig.SetCameras(("dock-1", "rtsp://cam/a"));
        rig.Start(_ => new Frames(1000, TimeSpan.FromMilliseconds(20)));
        Assert.True(await Eventually(() => rig.Sink.TrackFrames() > 0), "dock-1 should be seen");

        rig.SetCameras(("dock-1", "rtsp://cam/a"), ("dock-2", "rtsp://cam/b"));
        Assert.True(await Eventually(() => rig.Sink.Snapshot().Any(l => l.Contains("dock-2") && l.Contains("\"tracks\""))), "dock-2 appears when added to the file");

        rig.SetCameras(("dock-2", "rtsp://cam/b"));   // dock-1 removed
        Assert.True(await Eventually(() => rig.Sink.Snapshot().Any(l => l.Contains("dock-1") && l.Contains(CameraState.NoSignal))), "a removed alias is lost signal");
    }

    [Fact]
    public async Task Supervisor_ACameraWhoseFileDisappearsWhilePlaying_IsLostSignal_AndComesBackWhenTheFileReturns()
    {
        using var rig = new SupervisorRig();
        var video = rig.Video("line.mp4");
        rig.SetCameras(("dock-1", video));
        rig.Start(_ => new Frames(100000, TimeSpan.FromMilliseconds(20), endless: true));
        Assert.True(await Eventually(() => rig.Sink.TrackFrames() > 0));

        File.Delete(video);
        Assert.True(await Eventually(() => rig.Sink.States().Contains(CameraState.NoSignal)), "the missing recording is reported");

        rig.Video("line.mp4");
        var framesBefore = rig.Sink.TrackFrames();
        Assert.True(await Eventually(() => rig.Sink.States().LastOrDefault() == CameraState.Ok && rig.Sink.TrackFrames() > framesBefore), "it is seen again and reported ok");
    }

    [Fact]
    public async Task Supervisor_ARecordingThatEnds_IsLostSignal_AndIsNotReplayed()
    {
        using var rig = new SupervisorRig();
        var opens = 0;
        rig.SetCameras(("dock-1", rig.Video("line.mp4")));
        rig.Start(_ => { Interlocked.Increment(ref opens); return new Frames(3, TimeSpan.FromMilliseconds(15)); });

        Assert.True(await Eventually(() => rig.Sink.States().Contains(CameraState.NoSignal)), "the end of the recording is lost signal");
        await Task.Delay(300);
        Assert.Equal(1, opens);
        Assert.Equal(3, rig.Sink.TrackFrames());
    }

    [Fact]
    public async Task Supervisor_WithLoopOn_StartsARecordingOverWhenItEnds()
    {
        using var rig = new SupervisorRig();
        var opens = 0;
        rig.SetCameras(("dock-1", rig.Video("line.mp4")));
        rig.Start(_ => { Interlocked.Increment(ref opens); return new Frames(2, TimeSpan.FromMilliseconds(15)); }, loop: true);

        Assert.True(await Eventually(() => Volatile.Read(ref opens) >= 3));
        Assert.DoesNotContain(CameraState.NoSignal, rig.Sink.States());
    }

    [Fact]
    public async Task Supervisor_ASourceThatCannotBeOpened_IsLostSignal_AndIsTriedAgain()
    {
        using var rig = new SupervisorRig();
        var attempts = 0;
        rig.SetCameras(("dock-1", "rtsp://cam/a"));
        rig.Start(_ =>
        {
            if (Interlocked.Increment(ref attempts) < 3) throw new FrameSourceException("no route");
            return new Frames(1000, TimeSpan.FromMilliseconds(20));
        });

        Assert.True(await Eventually(() => rig.Sink.TrackFrames() > 0));
        Assert.Equal([CameraState.NoSignal, CameraState.Ok], rig.Sink.States());
    }

    [Fact]
    public void CamerasFile_ReadsOnlyStringSources_AndTreatsAMissingOrBrokenFileAsEmpty()
    {
        using var rig = new SupervisorRig();
        File.WriteAllText(rig.Cameras, """{"a":"/v/a.mp4","b":42,"c":"","d":"rtsp://x/y"}""");
        Assert.Equal(["a", "d"], CamerasFile.Read(rig.Cameras).Keys.Order());

        File.WriteAllText(rig.Cameras, "{not json");
        Assert.Empty(CamerasFile.Read(rig.Cameras));
        Assert.Empty(CamerasFile.Read(Path.Combine(rig.Dir, "missing.json")));
    }
}
