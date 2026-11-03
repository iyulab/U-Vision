namespace UVision.Api.OpcUa;

/// <summary>
/// OPC UA 서버 쪽 결과 보관소 하나 — 최근 결과 창과 «지금 서 있는 노드 관리자»를 쥐는 DI 싱글턴.
/// 사건 전송(<see cref="OpcUaTransport"/>)과 노드 관리자(서버 스택이 만든다)가 서로를 직접 모르고 이것을 거쳐 닿는다.
/// </summary>
public sealed class OpcUaResultHub(int resultWindow)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ResultsNodeManager? _manager;
    private DateTimeOffset? _lastEventAt;

    public OpcUaResultWindow Window { get; } = new(resultWindow);

    /// <summary>서버의 주소 공간이 지어져 사건을 올릴 수 있는가.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _manager is not null; }
    }

    /// <summary>마지막으로 사건을 올린 시각 — 없으면 null.</summary>
    public DateTimeOffset? LastEventAt
    {
        get { lock (_gate) return _lastEventAt; }
    }

    /// <summary>서버가 설 때까지 기다린다(시험 · 상태 보기용).</summary>
    public Task WaitUntilRunningAsync(CancellationToken cancellationToken = default) => _running.Task.WaitAsync(cancellationToken);

    internal void Attach(ResultsNodeManager manager)
    {
        lock (_gate)
            _manager = manager;
        _running.TrySetResult();
    }

    internal void Detach(ResultsNodeManager manager)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_manager, manager))
                _manager = null;
        }
    }

    /// <summary>
    /// 결과를 창에 넣고 사건을 올린다. 성공이면 null, 서버가 서 있지 않거나 올리지 못하면 오류 문구(던지지 않는다).
    /// 이미 창에 있는 판정 id 는 다시 올리지 않고 성공으로 친다 — outbox 가 최소 한 번 보내므로 같은 사건이 다시 올 수 있다.
    /// </summary>
    public string? Publish(OpcUaResult result)
    {
        ResultsNodeManager? manager;
        lock (_gate)
            manager = _manager;
        if (manager is null)
            return "OPC UA server not running";
        if (!Window.TryAdd(result))
            return null;
        try
        {
            manager.Raise(result);
        }
        catch (Exception ex)
        {
            // 올리지 못한 결과가 창에만 남으면 재시도가 «이미 있음» 으로 성공해 사건이 영영 안 나간다 — 되돌린다.
            Window.TryRemove(result.ResultId);
            return ex.GetType().Name + ": " + ex.Message;
        }
        lock (_gate)
            _lastEventAt = DateTimeOffset.UtcNow;
        return null;
    }
}
