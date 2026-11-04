using System.Net.Sockets;
using System.Text;
using UVision.Edge.Agent.Rules;
using UVision.Edge.Agent.Worker;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 워커 → 에이전트 수신(원장 D-105 W-1 · W-2): 줄 프로토콜을 읽는 순수 파서, 어떤 크기로 쪼개져 와도 줄을 맞추는 리더, 실제 유닉스 소켓 위의
/// 서버(순서 · 깨진 줄 · 끊김 · 새 연결의 승계 · 소켓 파일).
/// </summary>
public sealed class WorkerSocketTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uvw-" + Guid.NewGuid().ToString("N")[..8]);

    public WorkerSocketTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private const string Frame =
        """{"v":1,"type":"tracks","source_id":"dock-1","seq":42,"t":"2026-11-04T09:00:00.123+00:00","tracks":[{"id":"7","class":"box","score":0.91,"box":[0.12,0.30,0.10,0.18]},{"id":8,"class":"forklift","score":0.5,"box":[0,0,0.2,0.2]}]}""";

    private const string State =
        """{"v":1,"type":"source_state","source_id":"dock-1","state":"no_signal","t":"2026-11-04T09:00:05Z"}""";

    // --- 파서 ---

    [Fact]
    public void Parse_ATracksLine_BecomesAFrameWithGroundPoints()
    {
        Assert.True(WorkerProtocol.TryParse(B(Frame), out var message, out var reason), reason);

        var frame = Assert.IsType<TrackFrame>(message);
        Assert.Equal(("dock-1", 42L), (frame.CameraRef, frame.Seq));
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 9, 0, 0, 123, TimeSpan.Zero), frame.Time);
        Assert.Equal(["7", "8"], frame.Tracks.Select(t => t.TrackId));        // 숫자 id 도 문자열로 받는다
        Assert.Equal(("box", 0.91), (frame.Tracks[0].Class, frame.Tracks[0].Score));
        Assert.Equal(0.12 + 0.05, frame.Tracks[0].GroundX, 9);
        Assert.Equal(0.30 + 0.18, frame.Tracks[0].GroundY, 9);
    }

    [Fact]
    public void Parse_AStateLine_BecomesASourceStateMessage()
    {
        Assert.True(WorkerProtocol.TryParse(B(State), out var message, out _));

        var state = Assert.IsType<SourceStateMessage>(message);
        Assert.Equal(("dock-1", "no_signal", new DateTimeOffset(2026, 11, 4, 9, 0, 5, TimeSpan.Zero)), (state.CameraRef, state.State, state.Time));
    }

    [Fact]
    public void Parse_AnOffsetTimeKeepsTheInstant()
    {
        var line = Frame.Replace("2026-11-04T09:00:00.123+00:00", "2026-11-04T18:00:00.123+09:00");

        Assert.True(WorkerProtocol.TryParse(B(line), out var message, out _));
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 9, 0, 0, 123, TimeSpan.Zero).UtcDateTime, ((TrackFrame)message!).Time.UtcDateTime);
    }

    [Theory]
    [InlineData("not json", "JSON")]
    [InlineData("[1,2]", "객체")]
    [InlineData("""{"v":2,"type":"tracks","source_id":"a","t":"2026-11-04T09:00:00Z","tracks":[]}""", "판")]
    [InlineData("""{"type":"tracks","source_id":"a","t":"2026-11-04T09:00:00Z","tracks":[]}""", "판")]
    [InlineData("""{"v":1,"type":"tracks","t":"2026-11-04T09:00:00Z","tracks":[]}""", "source_id")]
    [InlineData("""{"v":1,"type":"tracks","source_id":"a","t":"2026-11-04T09:00:00","tracks":[]}""", "시간대")]
    [InlineData("""{"v":1,"type":"tracks","source_id":"a","tracks":[]}""", "시간대")]
    [InlineData("""{"v":1,"type":"boom","source_id":"a","t":"2026-11-04T09:00:00Z"}""", "type")]
    [InlineData("""{"v":1,"type":"source_state","source_id":"a","state":"exploded","t":"2026-11-04T09:00:00Z"}""", "state")]
    [InlineData("""{"v":1,"type":"tracks","source_id":"a","t":"2026-11-04T09:00:00Z","tracks":{}}""", "tracks")]
    [InlineData("""{"v":1,"type":"tracks","source_id":"a","t":"2026-11-04T09:00:00Z","tracks":[{"id":"1","class":"box","score":0.9,"box":[0,0,1]}]}""", "box")]
    [InlineData("""{"v":1,"type":"tracks","source_id":"a","t":"2026-11-04T09:00:00Z","tracks":[{"id":"1","class":"box","score":0.9,"box":[0,0,-1,1]}]}""", "w")]
    [InlineData("""{"v":1,"type":"tracks","source_id":"a","t":"2026-11-04T09:00:00Z","tracks":[{"id":"1","class":"box","score":"x","box":[0,0,1,1]}]}""", "score")]
    [InlineData("""{"v":1,"type":"tracks","source_id":"a","seq":-1,"t":"2026-11-04T09:00:00Z","tracks":[]}""", "seq")]
    public void Parse_RejectsWhatItDoesNotUnderstand_WithAReason(string line, string reasonFragment)
    {
        Assert.False(WorkerProtocol.TryParse(B(line), out var message, out var reason));
        Assert.Null(message);
        Assert.Contains(reasonFragment, reason);
    }

    [Fact]
    public void Parse_TooManyTracksIsRejected()
    {
        var tracks = string.Join(",", Enumerable.Range(0, WorkerProtocol.MaxTracks + 1).Select(i => $$"""{"id":"{{i}}","class":"b","score":1,"box":[0,0,1,1]}"""));
        var line = $$"""{"v":1,"type":"tracks","source_id":"a","t":"2026-11-04T09:00:00Z","tracks":[{{tracks}}]}""";

        Assert.False(WorkerProtocol.TryParse(B(line), out _, out var reason));
        Assert.Contains("개를 넘는다", reason);
    }

    // --- 줄 리더 ---

    /// <summary>호출마다 정해진 바이트 수만 돌려주는 스트림 — 줄이 읽기 경계에 걸쳐 오는 상황.</summary>
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

    private static async Task<List<string?>> LinesAsync(byte[] data, int chunk, int maxLine = 1024)
    {
        var lines = new List<string?>();
        await foreach (var l in NdjsonLines.ReadAsync(new Trickle(data, chunk), maxLine))
            lines.Add(l.TooLong ? null : Encoding.UTF8.GetString(l.Bytes!));
        return lines;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Lines_AreTheSameHoweverTheBytesArriveSplit(int chunk)
    {
        var lines = await LinesAsync(B("alpha\nbeta\r\n\n한글\n"), chunk);

        Assert.Equal(["alpha", "beta", "한글"], lines);   // CRLF 는 떼고, 빈 줄은 건너뛴다
    }

    [Fact]
    public async Task Lines_AnOverlongLineIsDroppedWholeAndTheNextOneStillReads()
    {
        var data = B("ok1\n" + new string('x', 5000) + "\nok2\n");
        foreach (var chunk in new[] { 1, 100, 8192 })
        {
            var lines = await LinesAsync(data, chunk, maxLine: 100);
            Assert.Equal(["ok1", null, "ok2"], lines);
        }
    }

    [Fact]
    public async Task Lines_APartialLastLineFromADyingWorkerIsDiscarded()
    {
        Assert.Equal(["whole"], await LinesAsync(B("whole\nhalf-writt"), 5));
    }

    // --- 서버 (실제 유닉스 소켓) ---

    private sealed class RecordingSink : IWorkerSink
    {
        public List<string> Log { get; } = [];
        public void OnFrame(TrackFrame frame) { lock (Log) Log.Add($"frame:{frame.CameraRef}:{frame.Seq}"); }
        public void OnSourceState(SourceStateMessage m) { lock (Log) Log.Add($"state:{m.CameraRef}:{m.State}"); }
        public void OnWorkerLost(DateTimeOffset at) { lock (Log) Log.Add("lost"); }
        public string[] Snapshot() { lock (Log) return [.. Log]; }
    }

    private static async Task UntilAsync(Func<bool> done, string what, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    private async Task<Socket> ConnectAsync(string path)
    {
        await UntilAsync(() => File.Exists(path), "소켓 파일이 생기지 않았다");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        return socket;
    }

    private static async Task SendAsync(Socket socket, string text) => await socket.SendAsync(B(text));

    private static string FrameLine(long seq) => Frame.Replace("\"seq\":42", $"\"seq\":{seq}") + "\n";

    [Fact]
    public async Task Server_DeliversInOrder_DropsGarbage_AndReportsALostWorkerWhenTheConnectionEnds()
    {
        var path = Path.Combine(_dir, "w.sock");
        var sink = new RecordingSink();
        var server = new WorkerSocketServer(path, sink);
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        using (var worker = await ConnectAsync(path))
        {
            await SendAsync(worker, FrameLine(1) + "garbage\n" + State + "\n" + FrameLine(2));
            await UntilAsync(() => sink.Snapshot().Length >= 3, "메시지가 오지 않았다");
        }
        await UntilAsync(() => sink.Snapshot().Contains("lost"), "끊김이 알려지지 않았다");

        Assert.Equal(["frame:dock-1:1", "state:dock-1:no_signal", "frame:dock-1:2", "lost"], sink.Snapshot());
        var stats = server.Stats;
        Assert.Equal((2L, 1L, 1L), (stats.Frames, stats.States, stats.Dropped));
        Assert.Contains("JSON", stats.LastDropReason);
        Assert.False(stats.Connected);

        await cts.CancelAsync();
        await run;
        Assert.False(File.Exists(path));   // 멈추면 소켓 파일을 치운다
    }

    [Fact]
    public async Task Server_ANewConnectionReplacesTheOldOne_WithoutALostWorkerInBetween()
    {
        var path = Path.Combine(_dir, "w.sock");
        var sink = new RecordingSink();
        var server = new WorkerSocketServer(path, sink);
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        using var first = await ConnectAsync(path);
        await SendAsync(first, FrameLine(1));
        await UntilAsync(() => sink.Snapshot().Length == 1, "첫 연결의 메시지");

        using var second = await ConnectAsync(path);                       // 워커가 재시작해 새로 붙었다
        await SendAsync(second, FrameLine(10));
        await UntilAsync(() => sink.Snapshot().Length == 2, "둘째 연결의 메시지");

        Assert.Equal(["frame:dock-1:1", "frame:dock-1:10"], sink.Snapshot());   // 승계 사이에 lost 가 없다
        Assert.Equal(2, server.Stats.Connections);

        second.Dispose();
        await UntilAsync(() => sink.Snapshot().Contains("lost"), "둘째 연결의 끊김");
        Assert.Single(sink.Snapshot(), "lost");                            // 소실은 한 번

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Server_ABrokenReceiverDoesNotKillReception()
    {
        var path = Path.Combine(_dir, "w.sock");
        var thrower = new ThrowingOnceSink();
        var server = new WorkerSocketServer(path, thrower);
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        using var worker = await ConnectAsync(path);
        await SendAsync(worker, FrameLine(1) + FrameLine(2));
        await UntilAsync(() => thrower.Seen.Count == 2, "둘째 메시지가 오지 않았다");

        Assert.Equal(1, server.Stats.Dropped);
        Assert.Contains("InvalidOperationException", server.Stats.LastDropReason);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Server_ALineWithTheWrongShapeDoesNotKillReception()
    {
        // JSON 으로는 맞지만 값의 종류가 틀린 줄(score 가 문자열) — 예전에는 파서가 던져 수신 전체가 죽었다.
        var path = Path.Combine(_dir, "w.sock");
        var sink = new RecordingSink();
        var server = new WorkerSocketServer(path, sink);
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        var wrongShape = """{"v":1,"type":"tracks","source_id":"a","t":"2026-11-04T09:00:00Z","tracks":[{"id":"1","class":"box","score":"x","box":[0,0,1,1]}]}""";

        using var worker = await ConnectAsync(path);
        await SendAsync(worker, wrongShape + "\n" + FrameLine(7));
        await UntilAsync(() => sink.Snapshot().Length == 1, "틀린 줄 뒤의 메시지가 오지 않았다");

        Assert.Equal(["frame:dock-1:7"], sink.Snapshot());
        Assert.Equal(1, server.Stats.Dropped);
        Assert.False(run.IsCompleted);   // 수신이 살아 있다

        await cts.CancelAsync();
        await run;
    }

    private sealed class ThrowingOnceSink : IWorkerSink
    {
        public List<long> Seen { get; } = [];
        public void OnFrame(TrackFrame frame)
        {
            lock (Seen) Seen.Add(frame.Seq);
            if (frame.Seq == 1) throw new InvalidOperationException("boom");
        }
        public void OnSourceState(SourceStateMessage message) { }
        public void OnWorkerLost(DateTimeOffset at) { }
    }

    [Fact]
    public async Task Server_ReplacesAStaleSocketFileLeftByADeadAgent()
    {
        var path = Path.Combine(_dir, "w.sock");
        await File.WriteAllTextAsync(path, "stale");
        var sink = new RecordingSink();
        var server = new WorkerSocketServer(path, sink);
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        using var worker = await ConnectAsync(path);
        await SendAsync(worker, FrameLine(1));
        await UntilAsync(() => sink.Snapshot().Length == 1, "남은 소켓 파일을 대신하지 못했다");

        await cts.CancelAsync();
        await run;
    }
}
