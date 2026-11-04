using System.Text.Json;

namespace UVision.Edge.Agent.Rules;

/// <summary>규칙 하나의 상태 기계 — 프레임이 올 때마다 사건을 0개 이상 낸다. 시간은 프레임의 시각뿐이다.</summary>
internal interface IRuleState
{
    string RuleId { get; }

    string ScenarioId { get; }

    /// <summary>규칙이 보는 카메라의 별칭 — 이 별칭의 프레임만 먹는다.</summary>
    string CameraRef { get; }

    /// <summary>프레임 하나를 먹이고 난 사건을 돌려준다(없으면 빈 열거).</summary>
    IEnumerable<RuleEvent> OnFrame(TrackFrame frame);
}

internal static class RuleData
{
    /// <summary>사건 <c>data</c> 를 만든다 — 키는 snake_case 로 적은 그대로.</summary>
    public static JsonElement Of(IReadOnlyDictionary<string, object?> fields) => JsonSerializer.SerializeToElement(fields);
}

/// <summary>
/// <c>count</c> · 구역(또는 프레임 전체) — 구역 안 <c>class</c> 개수가 <b>안정</b>일 때만 변화 사건을 낸다. 안정 = 같은 개수가
/// <c>stable_frames</c>(기본 5)개 프레임 연속이거나 <c>stable_seconds</c> 이상 이어짐. 한 프레임 깜빡이는 검출 · 경계 떨림은 안정 구간을
/// 못 채워 사건이 되지 않는다. 처음 안정된 개수는 <c>previous: null</c> 로 한 번 알린다(받는 쪽이 기준선을 알게).
/// </summary>
internal sealed class CountZoneState : IRuleState
{
    public const int DefaultStableFrames = 5;

    private readonly string _scenarioId;
    private readonly StreamRule _rule;
    private readonly string _zoneId;
    private readonly IReadOnlyList<IReadOnlyList<double>>? _polygon;

    private int? _reported;
    private int? _candidate;
    private DateTimeOffset _candidateSince;
    private int _candidateFrames;

    public CountZoneState(string scenarioId, StreamRule rule, string cameraRef, StreamZone? zone)
    {
        CameraRef = cameraRef;
        _scenarioId = scenarioId;
        _rule = rule;
        _zoneId = zone?.Id ?? "";
        _polygon = zone?.Polygon;
    }

    public string RuleId => _rule.Id;

    public string ScenarioId => _scenarioId;

    public string CameraRef { get; }

    public IEnumerable<RuleEvent> OnFrame(TrackFrame frame)
    {
        var count = ZoneCounter.CountInside(frame, _rule.Class, _polygon);

        if (_candidate != count)
        {
            _candidate = count;
            _candidateSince = frame.Time;
            _candidateFrames = 1;
        }
        else
        {
            _candidateFrames++;
        }

        var stable = _rule.StableSeconds is { } seconds
            ? (frame.Time - _candidateSince).TotalSeconds >= seconds
            : _candidateFrames >= (_rule.StableFrames ?? DefaultStableFrames);
        if (!stable || _reported == count)
            yield break;

        var previous = _reported;
        _reported = count;
        yield return new RuleEvent(_scenarioId, "count_changed", frame.Time, RuleData.Of(new Dictionary<string, object?>
        {
            ["rule"] = _rule.Id,
            ["source"] = _rule.Source,
            ["zone"] = _polygon is null ? null : _zoneId,
            ["class"] = _rule.Class,
            ["count"] = count,
            ["previous"] = previous,
            ["since"] = _candidateSince,
        }));
    }
}

/// <summary>
/// <c>count</c> · 선 — 트랙의 땅 점이 선분 a-b 를 가로지르는 것을 센다. 방향은 a→b 를 화면에서 바라볼 때 <b>오른쪽</b>으로 넘어가면 <c>in</c>,
/// 왼쪽으로 넘어가면 <c>out</c>. 선 양쪽 <see cref="Deadband"/>(정규화 거리) 안에 있는 동안은 어느 쪽에도 속하지 않는 것으로 보아 선 위를
/// 서성이는 트랙의 떨림이 입 · 출 사건을 쏟아내지 않고, 선분 <b>바깥</b>에서 선의 연장을 넘는 것은 통과가 아니다. 통과마다 사건 하나 +
/// 이 규칙이 시작한 뒤의 누적 <c>in</c> · <c>out</c>.
/// </summary>
internal sealed class CountLineState : IRuleState
{
    /// <summary>선에서 이만큼(프레임 폭의 1 %) 떨어져야 «그쪽에 있다» 로 본다.</summary>
    public const double Deadband = 0.01;

    /// <summary>이만큼 보이지 않은 트랙의 기억은 버린다(메모리가 자라지 않게). 다시 나타나면 새 트랙처럼 시작한다.</summary>
    public static readonly TimeSpan TrackMemory = TimeSpan.FromSeconds(30);

    private sealed class Memory
    {
        public int Side;
        public double AnchorX, AnchorY;
        public DateTimeOffset LastSeen;
    }

    private readonly string _scenarioId;
    private readonly StreamRule _rule;
    private readonly double _ax, _ay, _bx, _by;
    private readonly Dictionary<string, Memory> _tracks = new(StringComparer.Ordinal);
    private long _in;
    private long _out;

    public CountLineState(string scenarioId, StreamRule rule, string cameraRef, StreamLine line)
    {
        CameraRef = cameraRef;
        _scenarioId = scenarioId;
        _rule = rule;
        (_ax, _ay, _bx, _by) = (line.A[0], line.A[1], line.B[0], line.B[1]);
    }

    public string RuleId => _rule.Id;

    public string ScenarioId => _scenarioId;

    public string CameraRef { get; }

    public IEnumerable<RuleEvent> OnFrame(TrackFrame frame)
    {
        // 오래 보이지 않던 트랙의 기억은 이 프레임을 처리하기 «전에» 버린다 — 늦게 돌아온 트랙이 옛 위치와 견줘 통과로 세어지지 않게.
        foreach (var stale in _tracks.Where(kv => frame.Time - kv.Value.LastSeen > TrackMemory).Select(kv => kv.Key).ToList())
            _tracks.Remove(stale);

        var events = new List<RuleEvent>();
        foreach (var t in frame.Tracks)
        {
            if (t.Class != _rule.Class) continue;
            var distance = Geometry.SignedDistance(_ax, _ay, _bx, _by, t.GroundX, t.GroundY);
            var side = distance > Deadband ? 1 : distance < -Deadband ? -1 : 0;

            if (!_tracks.TryGetValue(t.TrackId, out var memory))
                _tracks[t.TrackId] = memory = new Memory();
            memory.LastSeen = frame.Time;
            if (side == 0) continue; // 선 위 — 어느 쪽인지 정해질 때까지 기준점을 옮기지 않는다

            if (memory.Side != 0 && memory.Side != side
                && Geometry.CrossesSegment(_ax, _ay, _bx, _by, memory.AnchorX, memory.AnchorY, t.GroundX, t.GroundY))
            {
                var direction = side > 0 ? "in" : "out";
                if (side > 0) _in++; else _out++;
                events.Add(new RuleEvent(_scenarioId, "count_changed", frame.Time, RuleData.Of(new Dictionary<string, object?>
                {
                    ["rule"] = _rule.Id,
                    ["source"] = _rule.Source,
                    ["line"] = _rule.Line,
                    ["class"] = _rule.Class,
                    ["direction"] = direction,
                    ["track_id"] = t.TrackId,
                    ["in"] = _in,
                    ["out"] = _out,
                })));
            }
            memory.Side = side;
            memory.AnchorX = t.GroundX;
            memory.AnchorY = t.GroundY;
        }
        return events;
    }
}
