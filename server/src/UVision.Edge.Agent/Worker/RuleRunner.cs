using UVision.Edge.Agent.Rules;

namespace UVision.Edge.Agent.Worker;

/// <summary>
/// 워커 소켓과 규칙 평가기와 내구 outbox 를 잇는다(원장 D-105 W-3) — 워커가 건넨 프레임을 <see cref="RuleEngine"/> 에 먹이고, 난 사건을
/// <see cref="AgentOutbox.EnqueueEvent"/> 로 디스크에 완결한 뒤 에이전트의 평소 전달(순서 · 최소 한 번 · 오프라인 견딤)에 맡긴다.
/// 프레임 단위 데이터는 여기서 사라지고(휘발) <b>사건만</b> 남는다.
/// <para>
/// 구성이 바뀌면 <see cref="Configure"/> 로 평가기를 다시 짓는다 — 소스와 규칙이 그대로인 시나리오는 상태를 이어받아 올라간 경보가 잊히지
/// 않는다. 엔진이 안 닿아도 에이전트가 디스크에 쥔 마지막 구성으로 규칙이 돈다(오프라인 견딤, W-6).
/// </para>
/// 평가기는 스레드 안전하지 않으므로 소켓 읽기 쪽 호출과 구성 교체를 한 자물쇠로 직렬화한다.
/// </summary>
public sealed class RuleRunner(AgentOutbox outbox, TimeProvider? clock = null) : IWorkerSink
{
    private readonly object _gate = new();
    private RuleEngine _engine = new([]);
    private long _events;
    private string? _configVersion;

    /// <summary>
    /// 지금 쥔 구성이 낡았는가를 답하는 함수 — 호스트가 «엔진과 마지막으로 확인한 지 <c>max_config_age</c> 가 지났는가» 로 정한다(원장 D-105 W-6).
    /// 참이면 규칙이 내는 모든 사건의 <c>data</c> 에 <c>config_stale: true</c> 가 달린다 — 규칙은 멈추지 않는다(경보가 조용히 사라지는 것이 더 위험하다);
    /// 낡은 규칙의 사건을 얼마나 믿을지는 받는 쪽이 정한다.
    /// </summary>
    public Func<bool>? IsConfigStale { get; set; }

    /// <summary>지금까지 outbox 에 넣은 사건 수(진단용).</summary>
    public long EventsEnqueued => Interlocked.Read(ref _events);

    /// <summary>평가기가 건너뛴 규칙과 사유(잘못된 구성) — 비어 있어야 정상이다.</summary>
    public IReadOnlyList<string> Skipped
    {
        get { lock (_gate) return [.. _engine.Skipped]; }
    }

    /// <summary>돌고 있는 규칙 수.</summary>
    public int RuleCount
    {
        get { lock (_gate) return _engine.RuleCount; }
    }

    /// <summary>워커가 열어야 할 카메라 별칭들.</summary>
    public IReadOnlyCollection<string> CameraRefs
    {
        get { lock (_gate) return [.. _engine.CameraRefs]; }
    }

    /// <summary>엔진이 준(또는 디스크에 있던) 구성으로 평가기를 다시 짓는다. 같은 판이면 아무것도 하지 않는다. 구성이 없으면 규칙 없음.</summary>
    public void Configure(EdgeConfig? config)
    {
        lock (_gate)
        {
            if (config is not null && config.Version == _configVersion) return;
            _configVersion = config?.Version;
            _engine = new RuleEngine(config?.Scenarios ?? [], carryOver: _engine);
            // 카메라가 이미 소실인 동안 새로 서거나 바뀐 시나리오에게 그 사실을 알린다.
            Enqueue(_engine.AnnounceInheritedSourceStates((clock ?? TimeProvider.System).GetUtcNow()));
        }
    }

    public void OnFrame(TrackFrame frame)
    {
        lock (_gate) Enqueue(_engine.Process(frame));
    }

    public void OnSourceState(SourceStateMessage message)
    {
        lock (_gate) Enqueue(_engine.OnSourceState(message));
    }

    public void OnWorkerLost(DateTimeOffset at)
    {
        lock (_gate) Enqueue(_engine.OnWorkerLost(at));
    }

    private void Enqueue(IReadOnlyList<RuleEvent> events)
    {
        if (events.Count == 0) return;
        var stale = IsConfigStale?.Invoke() == true;
        foreach (var e in events)
        {
            outbox.EnqueueEvent(e.ScenarioId, e.Kind, e.Time, stale ? WithStaleFlag(e.Data) : e.Data);
            Interlocked.Increment(ref _events);
        }
    }

    private static System.Text.Json.JsonElement WithStaleFlag(System.Text.Json.JsonElement data)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
        node["config_stale"] = true;
        return System.Text.Json.JsonSerializer.SerializeToElement(node);
    }
}
