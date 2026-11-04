namespace UVision.Edge.Agent.Rules;

/// <summary>구역(또는 프레임 전체) 안 한 종류의 개수 — <c>count</c> 와 <c>alarm</c> 이 같은 정의를 쓴다.</summary>
internal static class ZoneCounter
{
    public static int CountInside(TrackFrame frame, string cls, IReadOnlyList<IReadOnlyList<double>>? polygon)
    {
        var count = 0;
        foreach (var t in frame.Tracks)
            if (t.Class == cls && (polygon is null || Geometry.PointInPolygon(t.GroundX, t.GroundY, polygon)))
                count++;
        return count;
    }
}

/// <summary>
/// <c>alarm</c> — 구역(또는 프레임 전체) 안 개수가 조건(<c>min_count</c> 이상 또는 <c>max_count</c> 이하)을 <c>for_seconds</c> 이상
/// 계속 만족하면 <c>alarm_raised</c> 를 한 번 낸다. 올라간 뒤 조건이 깨진 상태가 <c>clear_seconds</c>(기본 <c>for_seconds</c>) 이상
/// 이어지면 <c>alarm_cleared</c> 를 한 번 낸다 — 올리는 쪽과 내리는 쪽이 각자 지속 시간을 채워야 하므로(히스테리시스) 경계에서 깜빡이는
/// 검출이 경보를 껐다 켰다 하지 않는다. 시간은 프레임 시각뿐이다: <b>프레임이 오지 않으면 평가도 없다</b> — 소스가 사라졌을 때
/// «부재 경보» 가 저절로 올라가지 않고 그 사실은 <c>source_state</c> 사건이 알린다.
/// </summary>
internal sealed class AlarmState : IRuleState
{
    private readonly string _scenarioId;
    private readonly StreamRule _rule;
    private readonly string? _zoneId;
    private readonly IReadOnlyList<IReadOnlyList<double>>? _polygon;
    private readonly TimeSpan _raiseAfter;
    private readonly TimeSpan _clearAfter;

    private bool _raised;
    private DateTimeOffset? _holdSince;     // 올라가기 전: 조건이 연속으로 참이 된 시각 · 올라간 뒤: 조건이 연속으로 거짓이 된 시각
    private DateTimeOffset _raisedAt;

    public AlarmState(string scenarioId, StreamRule rule, string cameraRef, StreamZone? zone)
    {
        _scenarioId = scenarioId;
        _rule = rule;
        CameraRef = cameraRef;
        _zoneId = zone?.Id;
        _polygon = zone?.Polygon;
        _raiseAfter = TimeSpan.FromSeconds(rule.ForSeconds ?? 0);
        _clearAfter = TimeSpan.FromSeconds(rule.ClearSeconds ?? rule.ForSeconds ?? 0);
    }

    public string RuleId => _rule.Id;

    public string CameraRef { get; }

    public IEnumerable<RuleEvent> OnFrame(TrackFrame frame)
    {
        var count = ZoneCounter.CountInside(frame, _rule.Class, _polygon);
        var met = _rule.MinCount is { } min ? count >= min : count <= (_rule.MaxCount ?? 0);

        // 올라가기 전에는 «참» 이 이어져야 하고, 올라간 뒤에는 «거짓» 이 이어져야 한다 — 방향만 다른 같은 상태 기계.
        var holding = _raised ? !met : met;
        if (!holding)
        {
            _holdSince = null;
            yield break;
        }

        _holdSince ??= frame.Time;
        var held = frame.Time - _holdSince.Value;
        if (held < (_raised ? _clearAfter : _raiseAfter))
            yield break;

        var since = _holdSince.Value;
        _holdSince = null;
        if (!_raised)
        {
            _raised = true;
            _raisedAt = frame.Time;
            yield return Event("alarm_raised", frame.Time, count, since, raisedAt: null);
        }
        else
        {
            _raised = false;
            yield return Event("alarm_cleared", frame.Time, count, since, _raisedAt);
        }
    }

    private RuleEvent Event(string kind, DateTimeOffset time, int count, DateTimeOffset since, DateTimeOffset? raisedAt)
    {
        var fields = new Dictionary<string, object?>
        {
            ["rule"] = _rule.Id,
            ["source"] = _rule.Source,
            ["zone"] = _zoneId,
            ["class"] = _rule.Class,
            ["count"] = count,
            ["min_count"] = _rule.MinCount,
            ["max_count"] = _rule.MaxCount,
            ["since"] = since, // 올림은 조건이 참이 되기 시작한 시각, 내림은 조건이 깨지기 시작한 시각
        };
        if (raisedAt is { } at) fields["raised_at"] = at;
        return new RuleEvent(_scenarioId, kind, time, RuleData.Of(fields));
    }
}
