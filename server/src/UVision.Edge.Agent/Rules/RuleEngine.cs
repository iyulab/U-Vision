namespace UVision.Edge.Agent.Rules;

/// <summary>워커가 알리는 카메라 상태 — <c>ok</c> · <c>no_signal</c>(영상이 끊김) · <c>degraded</c>(보이지만 품질이 나쁨).</summary>
public sealed record SourceStateMessage(string CameraRef, string State, DateTimeOffset Time);

public static class SourceStates
{
    public const string Ok = "ok";
    public const string NoSignal = "no_signal";
    public const string Degraded = "degraded";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Ok, NoSignal, Degraded };
}

/// <summary>
/// 규칙 평가기(원장 D-105 W-3) — 엔진이 준 구성(<see cref="EdgeScenarioConfig"/>)의 스트림 감시 시나리오를 읽어 규칙마다 상태 기계를 세우고,
/// 워커의 프레임을 먹여 <b>사건</b>을 낸다. 순수하다: 시계도 파일도 소켓도 모른다 — 시간은 프레임의 시각이고 입력은 호출자가 넣는다
/// (파일 재생 가짜 워커 · 시험이 하드웨어 없이 같은 코드를 돌린다). 호출은 직렬이라고 가정한다.
/// <para>
/// <b>카메라 별칭이 열쇠다</b>: 프레임과 소스 상태는 노드 로컬 카메라 별칭(<c>camera_ref</c>)으로 오고, 같은 별칭을 쓰는 모든 시나리오의 소스에
/// 간다. <b>소스 소실은 침묵이 아니라 사건이다</b>(W-2 · W-6): 워커가 <c>no_signal</c> 을 알리거나 연결이 끊기면(<see cref="OnWorkerLost"/>)
/// 그 카메라를 쓰는 시나리오마다 <c>source_state</c> 사건이 나가고, 프레임이 다시 오면 <c>ok</c> 로 돌아온다. 처음 상태는 <c>ok</c> 로 본다.
/// </para>
/// <para>
/// 구성을 믿되 깨지 않는다: 엔진 경계가 검증한 값이지만 에이전트는 구성 사본을 디스크에서 다시 읽으므로, 가리키는 소스 · 구역 · 선이
/// 없는 규칙은 던지지 않고 <see cref="Skipped"/> 에 사유를 남기고 건너뛴다(상태 보고로 알릴 수 있게).
/// </para>
/// </summary>
public sealed class RuleEngine
{
    private readonly List<IRuleState> _states = [];
    private readonly Dictionary<string, List<OutputFilter>?> _outputs = new(StringComparer.Ordinal);
    private readonly List<string> _skipped = [];
    private readonly Dictionary<string, List<(string ScenarioId, string SourceId)>> _cameras = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cameraState = new(StringComparer.Ordinal);

    private sealed record OutputFilter(string Event, string? Rule);

    public RuleEngine(IEnumerable<EdgeScenarioConfig> scenarios)
    {
        foreach (var scenario in scenarios)
        {
            if (scenario.Type != "monitoring") continue;
            _outputs[scenario.ScenarioId] = scenario.Outputs is null
                ? null
                : [.. scenario.Outputs.Select(o => new OutputFilter(o.Event, o.Rule))];
            foreach (var source in scenario.Sources ?? [])
            {
                if (!_cameras.TryGetValue(source.CameraRef, out var list))
                    _cameras[source.CameraRef] = list = [];
                list.Add((scenario.ScenarioId, source.Id));
            }
            foreach (var rule in scenario.Rules ?? [])
                if (Build(scenario, rule) is { } state)
                    _states.Add(state);
        }
    }

    /// <summary>건너뛴 규칙과 사유 — 비어 있어야 정상이다.</summary>
    public IReadOnlyList<string> Skipped => _skipped;

    /// <summary>이 평가기가 돌리는 규칙 수.</summary>
    public int RuleCount => _states.Count;

    /// <summary>시나리오가 보는 카메라 별칭들 — 워커가 열어야 할 카메라 목록이다.</summary>
    public IReadOnlyCollection<string> CameraRefs => _cameras.Keys;

    private IRuleState? Build(EdgeScenarioConfig scenario, StreamRule rule)
    {
        var source = scenario.Sources?.FirstOrDefault(s => s.Id == rule.Source);
        if (source is null)
            return Skip(scenario, rule, $"소스 {rule.Source} 가 없다");

        StreamZone? zone = null;
        if (rule.Zone is not null)
        {
            zone = source.Zones?.FirstOrDefault(z => z.Id == rule.Zone);
            if (zone is not { Polygon.Count: >= 3 } || zone.Polygon.Any(p => p.Count != 2))
                return Skip(scenario, rule, $"구역 {rule.Zone} 이 없거나 꼭짓점이 3개 미만이다");
        }

        switch (rule.Kind)
        {
            case "count" when rule.Line is not null:
                return source.Lines?.FirstOrDefault(l => l.Id == rule.Line) is { A.Count: 2, B.Count: 2 } line
                    ? new CountLineState(scenario.ScenarioId, rule, source.CameraRef, line)
                    : Skip(scenario, rule, $"선 {rule.Line} 이 없거나 끝점이 둘이 아니다");
            case "count":
                return new CountZoneState(scenario.ScenarioId, rule, source.CameraRef, zone);
            case "alarm" when rule.ForSeconds is > 0 && (rule.MinCount is null) != (rule.MaxCount is null):
                return new AlarmState(scenario.ScenarioId, rule, source.CameraRef, zone);
            case "alarm":
                return Skip(scenario, rule, "alarm 은 for_seconds 와 min_count/max_count 중 하나가 필요하다");
            default:
                return Skip(scenario, rule, $"모르는 규칙 종류 {rule.Kind}");
        }
    }

    private IRuleState? Skip(EdgeScenarioConfig scenario, StreamRule rule, string reason)
    {
        _skipped.Add($"{scenario.ScenarioId}/{rule.Id}: {reason}");
        return null;
    }

    /// <summary>프레임 하나를 먹이고, 그 프레임에서 난 사건(출력 필터를 통과한 것만)을 돌려준다.</summary>
    public IReadOnlyList<RuleEvent> Process(TrackFrame frame)
    {
        // 프레임이 왔다 = 영상이 돌아왔다. 워커가 따로 ok 를 알리지 않아도 소실 상태가 풀린다(degraded 는 워커가 알릴 때까지 유지).
        var events = _cameraState.TryGetValue(frame.CameraRef, out var state) && state == SourceStates.NoSignal
            ? [.. OnSourceState(new SourceStateMessage(frame.CameraRef, SourceStates.Ok, frame.Time))]
            : new List<RuleEvent>();

        foreach (var rule in _states)
        {
            if (rule.CameraRef != frame.CameraRef) continue;
            foreach (var e in rule.OnFrame(frame))
                if (Allowed(e, rule.RuleId))
                    events.Add(e);
        }
        return events;
    }

    /// <summary>
    /// 워커가 카메라 상태를 알렸다 — 바뀐 경우에만(처음 상태는 <c>ok</c> 로 본다) 그 카메라를 쓰는 시나리오마다 <c>source_state</c> 사건 하나.
    /// 모르는 카메라 · 모르는 상태 · 같은 상태의 반복은 사건이 없다.
    /// </summary>
    public IReadOnlyList<RuleEvent> OnSourceState(SourceStateMessage message)
    {
        if (!SourceStates.All.Contains(message.State) || !_cameras.TryGetValue(message.CameraRef, out var users))
            return [];
        var previous = _cameraState.GetValueOrDefault(message.CameraRef, SourceStates.Ok);
        if (previous == message.State)
            return [];
        _cameraState[message.CameraRef] = message.State;

        var events = new List<RuleEvent>();
        foreach (var (scenarioId, sourceId) in users)
        {
            var e = new RuleEvent(scenarioId, "source_state", message.Time, RuleData.Of(new Dictionary<string, object?>
            {
                ["source"] = sourceId,
                ["camera"] = message.CameraRef,
                ["state"] = message.State,
                ["previous"] = previous,
            }));
            if (Allowed(e, ruleId: ""))
                events.Add(e);
        }
        return events;
    }

    /// <summary>워커와의 연결이 끊겼다 — 알려진 모든 카메라가 소실로 바뀐다(이미 소실인 것은 그대로). 연결이 돌아와 프레임이 오면 풀린다.</summary>
    public IReadOnlyList<RuleEvent> OnWorkerLost(DateTimeOffset time) =>
        [.. _cameras.Keys.SelectMany(camera => OnSourceState(new SourceStateMessage(camera, SourceStates.NoSignal, time)))];

    // outputs 생략 = 규칙이 낼 수 있는 모든 사건. 있으면 목록에 있는 것만(rule 을 적었으면 그 규칙의 것만; source_state 는 규칙이 없다).
    private bool Allowed(RuleEvent e, string ruleId) =>
        !_outputs.TryGetValue(e.ScenarioId, out var filters) || filters is null
        || filters.Any(f => f.Event == e.Kind && (f.Rule is null || f.Rule == ruleId));
}
