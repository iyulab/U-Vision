namespace UVision.Edge.Agent.Rules;

/// <summary>
/// 규칙 평가기(원장 D-105 W-3) — 엔진이 준 구성(<see cref="EdgeScenarioConfig"/>)의 스트림 감시 시나리오를 읽어 규칙마다 상태 기계를 세우고,
/// 워커의 프레임을 먹여 <b>사건</b>을 낸다. 순수하다: 시계도 파일도 소켓도 모른다 — 시간은 프레임의 시각이고 입력은 호출자가 넣는다
/// (파일 재생 가짜 워커 · 시험이 하드웨어 없이 같은 코드를 돌린다). 한 프레임에 대한 호출은 직렬이라고 가정한다.
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

    private sealed record OutputFilter(string Event, string? Rule);

    public RuleEngine(IEnumerable<EdgeScenarioConfig> scenarios)
    {
        foreach (var scenario in scenarios)
        {
            if (scenario.Type != "monitoring") continue;
            _outputs[scenario.ScenarioId] = scenario.Outputs is null
                ? null
                : [.. scenario.Outputs.Select(o => new OutputFilter(o.Event, o.Rule))];
            foreach (var rule in scenario.Rules ?? [])
            {
                if (Build(scenario, rule) is { } state)
                    _states.Add(state);
            }
        }
    }

    /// <summary>건너뛴 규칙과 사유 — 비어 있어야 정상이다.</summary>
    public IReadOnlyList<string> Skipped => _skipped;

    /// <summary>이 평가기가 돌리는 규칙 수.</summary>
    public int RuleCount => _states.Count;

    private IRuleState? Build(EdgeScenarioConfig scenario, StreamRule rule)
    {
        var source = scenario.Sources?.FirstOrDefault(s => s.Id == rule.Source);
        if (source is null)
            return Skip(scenario, rule, $"소스 {rule.Source} 가 없다");

        switch (rule.Kind)
        {
            case "count" when rule.Line is not null:
                return source.Lines?.FirstOrDefault(l => l.Id == rule.Line) is { A.Count: 2, B.Count: 2 } line
                    ? new CountLineState(scenario.ScenarioId, rule, line)
                    : Skip(scenario, rule, $"선 {rule.Line} 이 없거나 끝점이 둘이 아니다");
            case "count":
                if (rule.Zone is null)
                    return new CountZoneState(scenario.ScenarioId, rule, zone: null);
                return source.Zones?.FirstOrDefault(z => z.Id == rule.Zone) is { Polygon.Count: >= 3 } zone
                       && zone.Polygon.All(p => p.Count == 2)
                    ? new CountZoneState(scenario.ScenarioId, rule, zone)
                    : Skip(scenario, rule, $"구역 {rule.Zone} 이 없거나 꼭짓점이 3개 미만이다");
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
        var events = new List<RuleEvent>();
        foreach (var state in _states)
        {
            if (state.SourceId != frame.SourceId) continue;
            foreach (var e in state.OnFrame(frame))
                if (Allowed(e, state.RuleId))
                    events.Add(e);
        }
        return events;
    }

    // outputs 생략 = 규칙이 낼 수 있는 모든 사건. 있으면 목록에 있는 것만(rule 을 적었으면 그 규칙의 것만).
    private bool Allowed(RuleEvent e, string ruleId) =>
        !_outputs.TryGetValue(e.ScenarioId, out var filters) || filters is null
        || filters.Any(f => f.Event == e.Kind && (f.Rule is null || f.Rule == ruleId));
}
