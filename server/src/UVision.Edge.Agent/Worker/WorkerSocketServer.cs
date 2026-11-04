using System.Net.Sockets;
using UVision.Edge.Agent.Rules;

namespace UVision.Edge.Agent.Worker;

/// <summary>워커가 보낸 것을 받는 쪽 — 호출은 항상 한 번에 하나, 도착 순서대로다(직렬).</summary>
public interface IWorkerSink
{
    void OnFrame(TrackFrame frame);

    void OnSourceState(SourceStateMessage message);

    /// <summary>워커와의 연결이 끊겼다(워커가 닫았거나 죽었다) — 새 연결이 이 연결을 대신한 경우는 부르지 않는다.</summary>
    void OnWorkerLost(DateTimeOffset at);
}

/// <summary>수신 통계 — 진단용이고 상태 보고에 실을 수 있다.</summary>
public sealed record WorkerSocketStats(
    long Connections, long Frames, long States, long Dropped, string? LastDropReason, bool Connected, DateTimeOffset? LastMessageAt = null);

public sealed class WorkerSocketOptions
{
    /// <summary>
    /// 소켓 파일의 Unix 권한 — 기본은 소유자만(<c>0600</c>). 워커 컨테이너가 다른 uid 로 돌면 공유 볼륨의 그룹 권한을 맞춰 넓힌다.
    /// Windows 에는 적용하지 않는다.
    /// </summary>
    public UnixFileMode FileMode { get; set; } = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public int MaxLineBytes { get; set; } = WorkerProtocol.MaxLineBytes;
}

/// <summary>
/// 워커 → 에이전트 수신(원장 D-105 W-1) — 에이전트가 <b>유닉스 도메인 소켓</b> 하나를 듣고 워커가 NDJSON(<see cref="WorkerProtocol"/>)을 흘린다.
/// 포트가 열리지 않고 새 의존성 · 프로세스가 없다. 프레임 단위 데이터는 <b>휘발</b>이다 — 연결이 끊기면 사라지고, 규칙이 낸 사건만 내구 outbox 로 간다.
/// <list type="bullet">
/// <item>워커는 하나다: 새 연결이 오면 옛 연결을 닫고 그 자리를 잇는다(죽은 줄 모르고 남은 연결이 새 워커를 막지 않게).
/// 옛 연결의 마지막 처리가 끝난 뒤에야 새 연결을 읽는다 — 순서가 섞이지 않는다.</item>
/// <item>깨진 줄 · 모르는 판 · 한도 넘은 줄은 버리고 센다(<see cref="WorkerSocketStats.Dropped"/>). 연결을 끊지 않는다.</item>
/// <item>연결이 끝나면(EOF · 오류) <see cref="IWorkerSink.OnWorkerLost"/> — 규칙이 소스 소실을 사건으로 만든다.</item>
/// </list>
/// </summary>
public sealed class WorkerSocketServer(string path, IWorkerSink sink, WorkerSocketOptions? options = null, TimeProvider? clock = null)
{
    private readonly WorkerSocketOptions _options = options ?? new WorkerSocketOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _connections, _frames, _states, _dropped;
    private volatile string? _lastDrop;
    private volatile bool _connected;
    private long _lastMessageTicks;   // UTC ticks, 0 = 아직 없음

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public WorkerSocketStats Stats =>
        new(Interlocked.Read(ref _connections), Interlocked.Read(ref _frames), Interlocked.Read(ref _states),
            Interlocked.Read(ref _dropped), _lastDrop, _connected,
            Interlocked.Read(ref _lastMessageTicks) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null);

    /// <summary>소켓을 열고 받는다 — <paramref name="ct"/> 가 취소될 때까지. 취소로 끝나면 소켓 파일을 지운다.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path)) File.Delete(Path); // 죽은 에이전트가 남긴 소켓 파일

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(Path));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, _options.FileMode);
        listener.Listen(4);

        CancellationTokenSource? currentCts = null;
        Task current = Task.CompletedTask;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(ct);

                // 새 연결이 옛 연결을 대신한다 — 옛 처리가 끝난 뒤에 새 연결을 읽기 시작한다.
                if (currentCts is not null)
                {
                    await currentCts.CancelAsync();
                    await current;
                    currentCts.Dispose();
                }
                currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                current = HandleAsync(client, currentCts.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 정상 종료
        }
        finally
        {
            if (currentCts is not null)
            {
                await currentCts.CancelAsync();
                await current;
                currentCts.Dispose();
            }
            try { File.Delete(Path); } catch (IOException) { /* 이미 없거나 못 지우면 다음 시작이 지운다 */ }
        }
    }

    private async Task HandleAsync(Socket client, CancellationToken ct)
    {
        Interlocked.Increment(ref _connections);
        _connected = true;
        var superseded = false;
        try
        {
            using (client)
            await using (var stream = new NetworkStream(client, ownsSocket: false))
            {
                await foreach (var line in NdjsonLines.ReadAsync(stream, _options.MaxLineBytes, ct))
                {
                    if (line.TooLong) { Drop("줄이 한도를 넘는다"); continue; }
                    try
                    {
                        if (!WorkerProtocol.TryParse(line.Bytes, out var message, out var reason)) { Drop(reason!); continue; }
                        switch (message)
                        {
                            case TrackFrame frame:
                                Interlocked.Increment(ref _frames);
                                Interlocked.Exchange(ref _lastMessageTicks, _clock.GetUtcNow().UtcTicks);
                                sink.OnFrame(frame);
                                break;
                            case SourceStateMessage state:
                                Interlocked.Increment(ref _states);
                                Interlocked.Exchange(ref _lastMessageTicks, _clock.GetUtcNow().UtcTicks);
                                sink.OnSourceState(state);
                                break;
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // 받는 쪽의 결함이 수신 전체를 죽이지 않는다 — 그 메시지만 버리고 센다.
                        Drop($"받는 쪽이 던졌다: {e.GetType().Name}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            superseded = true; // 새 연결이 대신했거나 에이전트가 멈춘다 — 워커가 사라진 것이 아니다(멈춤은 호출부가 안다)
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // 워커가 비정상으로 끊었다 — 소실로 다룬다.
        }
        finally
        {
            if (!superseded)
            {
                _connected = false;
                sink.OnWorkerLost(_clock.GetUtcNow());
            }
        }
    }

    private void Drop(string reason)
    {
        Interlocked.Increment(ref _dropped);
        _lastDrop = reason;
    }
}
