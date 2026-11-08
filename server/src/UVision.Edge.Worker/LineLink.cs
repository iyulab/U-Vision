using System.Net.Sockets;
using System.Text;

namespace UVision.Edge.Worker;

/// <summary>Where protocol lines go. Frame data is volatile by design: a line that cannot be delivered is dropped, never queued.</summary>
public interface ILineSink
{
    /// <summary>Counts up each time the connection to the agent is (re)established - a camera re-announces a non-ok state when it changes.</summary>
    int Connections { get; }

    /// <summary>Sends one line. Returns false when the agent is not reachable right now (the line is dropped).</summary>
    ValueTask<bool> SendAsync(string line, CancellationToken ct);
}

/// <summary>
/// The agent's unix domain socket. The worker is the connecting side: if the agent is not there (yet, or any more) lines are dropped and a new
/// connection is tried at most once a second. Only one task writes at a time so lines are never interleaved.
/// </summary>
public sealed class UnixSocketLink(string path, TimeProvider clock) : ILineSink, IDisposable
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Socket? _socket;
    private long _lastAttempt = long.MinValue;
    private int _connections;

    public int Connections => Volatile.Read(ref _connections);

    public async ValueTask<bool> SendAsync(string line, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_socket is null && !TryConnect()) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                var sent = 0;
                while (sent < bytes.Length)
                    sent += await _socket!.SendAsync(bytes.AsMemory(sent), SocketFlags.None, ct);
                return true;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
            {
                Drop();
                return false;
            }
        }
        finally { _gate.Release(); }
    }

    private bool TryConnect()
    {
        var now = clock.GetTimestamp();
        if (_lastAttempt != long.MinValue && clock.GetElapsedTime(_lastAttempt, now) < RetryEvery) return false;
        _lastAttempt = now;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            socket.Dispose();
            return false;
        }
        _socket = socket;
        Interlocked.Increment(ref _connections);
        return true;
    }

    private void Drop()
    {
        _socket?.Dispose();
        _socket = null;
    }

    public void Dispose()
    {
        Drop();
        _gate.Dispose();
    }
}
