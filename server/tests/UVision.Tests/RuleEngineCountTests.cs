using System.Text.Json;
using UVision.Edge.Agent;
using UVision.Edge.Agent.Rules;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 규칙 평가기의 <c>count</c>(원장 D-105 W-3 · W-4) — 하드웨어도 시계도 없이 트랙 프레임을 직접 먹여 안정 구간 · 경계 떨림 · 선 통과를
/// 가린다. 시간은 프레임의 시각뿐이라 시험이 시간을 마음대로 흘린다.
/// </summary>
public class RuleEngineCountTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    private static List<List<double>> Shelf() => [[0.1, 0.1], [0.5, 0.1], [0.5, 0.9], [0.1, 0.9]];   // 왼쪽 절반

    // 수직선 x=0.5, a(위)→b(아래). 화면에서 a→b 를 바라볼 때 오른쪽 = 화면 왼쪽(x 가 작은 쪽)이 in.
    private static StreamLine Door() => new() { Id = "door", A = [0.5, 0.0], B = [0.5, 1.0] };

    private static EdgeScenarioConfig Scenario(
        IEnumerable<StreamRule> rules, List<StreamOutput>? outputs = null, string id = "wh") => new()
    {
        ScenarioId = id,
        Type = "monitoring",
        Sources = [new StreamSource { Id = "cam1", CameraRef = "dock-1", Zones = [new StreamZone { Id = "shelf", Polygon = Shelf() }], Lines = [Door()] }],
        Rules = [.. rules],
        Outputs = outputs,
    };

    private static StreamRule ZoneCount(string id = "stock", int? frames = null, double? seconds = null, string? zone = "shelf") => new()
    {
        Id = id, Kind = "count", Source = "cam1", Zone = zone, Class = "box", StableFrames = frames, StableSeconds = seconds,
    };

    private static StreamRule LineCount(string id = "traffic", string cls = "forklift") => new()
    {
        Id = id, Kind = "count", Source = "cam1", Line = "door", Class = cls,
    };

    // 바닥 점이 (gx, gy) 가 되도록 상자를 둔다(폭 0.1 · 높이 0.1).
    private static TrackSample Box(string id, double gx, double gy, string cls = "box") =>
        new(id, cls, 0.9, gx - 0.05, gy - 0.1, 0.1, 0.1);

    private static TrackFrame Frame(int n, double seconds, params TrackSample[] tracks) =>
        new("dock-1", n, T0.AddSeconds(seconds), tracks);   // 프레임은 카메라 별칭으로 온다(소스 id 가 아니라)

    private static T Get<T>(RuleEvent e, string key) => e.Data.GetProperty(key).Deserialize<T>()!;

    // --- 기하 ---

    [Fact]
    public void PointInPolygon_HandlesConvexConcaveAndOutside()
    {
        IReadOnlyList<IReadOnlyList<double>> square = Shelf().Select(p => (IReadOnlyList<double>)p).ToList();
        Assert.True(Geometry.PointInPolygon(0.3, 0.5, square));
        Assert.False(Geometry.PointInPolygon(0.7, 0.5, square));
        Assert.False(Geometry.PointInPolygon(0.3, 0.95, square));

        // 오목한 «ㄷ» — 파인 곳은 밖이다.
        IReadOnlyList<IReadOnlyList<double>> u = new List<IReadOnlyList<double>>
        {
            new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, 1.0 }, new[] { 0.7, 1.0 },
            new[] { 0.7, 0.4 }, new[] { 0.3, 0.4 }, new[] { 0.3, 1.0 }, new[] { 0.0, 1.0 },
        };
        Assert.True(Geometry.PointInPolygon(0.1, 0.8, u));
        Assert.False(Geometry.PointInPolygon(0.5, 0.8, u));   // 파인 곳
        Assert.True(Geometry.PointInPolygon(0.5, 0.2, u));
    }

    [Fact]
    public void SignedDistance_PositiveIsTheRightHandSideLookingFromAToB()
    {
        // a(0,0)→b(1,0): 화면에서 오른쪽을 보고 서면 오른손은 아래(y 큰 쪽)다.
        Assert.True(Geometry.SignedDistance(0, 0, 1, 0, 0.5, 0.3) > 0);
        Assert.True(Geometry.SignedDistance(0, 0, 1, 0, 0.5, -0.3) < 0);
        Assert.Equal(0.3, Geometry.SignedDistance(0, 0, 1, 0, 0.5, 0.3), 9);
    }

    [Fact]
    public void CrossesSegment_OnlyInsideTheFiniteSegment()
    {
        Assert.True(Geometry.CrossesSegment(0.5, 0.2, 0.5, 0.8, 0.7, 0.5, 0.3, 0.5));    // 선분 가운데를 가로지름
        Assert.False(Geometry.CrossesSegment(0.5, 0.2, 0.5, 0.8, 0.7, 0.95, 0.3, 0.95)); // 연장선을 넘었을 뿐
        Assert.False(Geometry.CrossesSegment(0.5, 0.2, 0.5, 0.8, 0.7, 0.5, 0.8, 0.5));   // 같은 쪽
    }

    // --- 구역 안 개수 ---

    [Fact]
    public void Zone_FirstStableCountIsAnnouncedOnceAsABaseline()
    {
        var engine = new RuleEngine([Scenario([ZoneCount(frames: 3)])]);
        var events = new List<RuleEvent>();
        for (var n = 0; n < 6; n++)
            events.AddRange(engine.Process(Frame(n, n, Box("a", 0.3, 0.5), Box("b", 0.2, 0.6))));

        var e = Assert.Single(events);
        Assert.Equal("count_changed", e.Kind);
        Assert.Equal("wh", e.ScenarioId);
        Assert.Equal(2, Get<int>(e, "count"));
        Assert.Equal(JsonValueKind.Null, e.Data.GetProperty("previous").ValueKind);   // 기준선 — 이전 값 없음
        Assert.Equal(T0.AddSeconds(2), e.Time);                                        // 세 번째 프레임에서 안정
        Assert.Equal("shelf", Get<string>(e, "zone"));
    }

    [Fact]
    public void Zone_AFlickeringDetectionNeverBecomesAnEvent()
    {
        var engine = new RuleEngine([Scenario([ZoneCount(frames: 4)])]);
        var events = new List<RuleEvent>();
        // 상자 하나가 안정(4프레임) → 한 프레임만 둘로 깜빡 → 다시 하나: 사건은 기준선 하나뿐.
        for (var n = 0; n < 4; n++) events.AddRange(engine.Process(Frame(n, n, Box("a", 0.3, 0.5))));
        events.AddRange(engine.Process(Frame(4, 4, Box("a", 0.3, 0.5), Box("ghost", 0.2, 0.5))));
        for (var n = 5; n < 12; n++) events.AddRange(engine.Process(Frame(n, n, Box("a", 0.3, 0.5))));

        Assert.Equal(1, Get<int>(Assert.Single(events), "count"));
    }

    [Fact]
    public void Zone_AChangeIsReportedOnlyAfterItHoldsForTheWindow_WithThePreviousCount()
    {
        var engine = new RuleEngine([Scenario([ZoneCount(frames: 3)])]);
        var events = new List<RuleEvent>();
        for (var n = 0; n < 3; n++) events.AddRange(engine.Process(Frame(n, n, Box("a", 0.3, 0.5))));
        for (var n = 3; n < 5; n++) events.AddRange(engine.Process(Frame(n, n, Box("a", 0.3, 0.5), Box("b", 0.2, 0.5))));
        Assert.Single(events);                                                    // 둘이 된 지 2프레임 — 아직
        events.AddRange(engine.Process(Frame(5, 5, Box("a", 0.3, 0.5), Box("b", 0.2, 0.5))));

        Assert.Equal(2, events.Count);
        Assert.Equal((2, 1), (Get<int>(events[1], "count"), Get<int>(events[1], "previous")));
        Assert.Equal(T0.AddSeconds(3), Get<DateTimeOffset>(events[1], "since"));  // 둘이 되기 시작한 프레임
    }

    [Fact]
    public void Zone_StableSecondsIsMeasuredOnFrameTime_NotOnFrameCount()
    {
        var engine = new RuleEngine([Scenario([ZoneCount(seconds: 5)])]);
        // 프레임 간격이 1 초, 5 초 안정 → 여섯 번째 프레임(t=5)에서야 안정. 프레임 수와 무관.
        var events = new List<RuleEvent>();
        for (var n = 0; n < 5; n++) events.AddRange(engine.Process(Frame(n, n, Box("a", 0.3, 0.5))));
        Assert.Empty(events);
        events.AddRange(engine.Process(Frame(5, 5, Box("a", 0.3, 0.5))));
        Assert.Equal(T0.AddSeconds(5), Assert.Single(events).Time);

        // 프레임이 드문 스트림: 단 두 프레임이라도 시간이 충분하면 안정(느린 카메라).
        var slow = new RuleEngine([Scenario([ZoneCount(seconds: 5)])]);
        Assert.Empty(slow.Process(Frame(0, 0, Box("a", 0.3, 0.5))));
        Assert.Single(slow.Process(Frame(1, 6, Box("a", 0.3, 0.5))));
    }

    [Fact]
    public void Zone_OnlyTheRulesClassInsideTheZoneIsCounted_AndNoZoneMeansTheWholeFrame()
    {
        var inZone = new RuleEngine([Scenario([ZoneCount(frames: 1)])]);
        var wholeFrame = new RuleEngine([Scenario([ZoneCount(frames: 1, zone: null)])]);
        var frame = Frame(0, 0,
            Box("in", 0.3, 0.5),                         // 구역 안 box
            Box("out", 0.8, 0.5),                        // 구역 밖 box
            Box("other", 0.3, 0.6, cls: "forklift"));    // 구역 안이지만 다른 종류

        Assert.Equal(1, Get<int>(Assert.Single(inZone.Process(frame)), "count"));
        var all = Assert.Single(wholeFrame.Process(frame));
        Assert.Equal(2, Get<int>(all, "count"));
        Assert.Equal(JsonValueKind.Null, all.Data.GetProperty("zone").ValueKind);
    }

    // --- 선 통과 ---

    [Fact]
    public void Line_CountsCrossingsInBothDirections_WithRunningTotals()
    {
        var engine = new RuleEngine([Scenario([LineCount()])]);
        var events = new List<RuleEvent>();
        // 화면 오른쪽(x 큼) → 왼쪽(x 작음): a→b 오른쪽 = x 작은 쪽이므로 in.
        events.AddRange(engine.Process(Frame(0, 0, Box("f1", 0.7, 0.5, "forklift"))));
        events.AddRange(engine.Process(Frame(1, 1, Box("f1", 0.3, 0.5, "forklift"))));
        // 다시 되돌아가면 out.
        events.AddRange(engine.Process(Frame(2, 2, Box("f1", 0.7, 0.5, "forklift"))));

        Assert.Equal(["in", "out"], events.Select(e => Get<string>(e, "direction")));
        Assert.Equal((1L, 0L), (Get<long>(events[0], "in"), Get<long>(events[0], "out")));
        Assert.Equal((1L, 1L), (Get<long>(events[1], "in"), Get<long>(events[1], "out")));
        Assert.Equal("f1", Get<string>(events[0], "track_id"));
        Assert.Equal("door", Get<string>(events[0], "line"));
    }

    [Fact]
    public void Line_HoveringOnTheLineIsNotACrossing_AndSkippingOverOnlyOnceIs()
    {
        var engine = new RuleEngine([Scenario([LineCount()])]);
        var events = new List<RuleEvent>();
        events.AddRange(engine.Process(Frame(0, 0, Box("f1", 0.6, 0.5, "forklift"))));
        // 선(x=0.5) 근처 ±0.01 안을 서성이다가
        foreach (var (n, x) in new[] { (1, 0.505), (2, 0.498), (3, 0.503), (4, 0.497), (5, 0.501) })
            events.AddRange(engine.Process(Frame(n, n, Box("f1", x, 0.5, "forklift"))));
        Assert.Empty(events);
        // 시작했던 쪽으로 돌아가면 아무 통과도 아니다
        events.AddRange(engine.Process(Frame(6, 6, Box("f1", 0.6, 0.5, "forklift"))));
        Assert.Empty(events);
        // 그러나 반대쪽으로 확실히 넘어가면 한 번 센다
        events.AddRange(engine.Process(Frame(7, 7, Box("f1", 0.4, 0.5, "forklift"))));
        Assert.Equal("in", Get<string>(Assert.Single(events), "direction"));
    }

    [Fact]
    public void Line_GoingAroundTheEndOfTheSegmentIsNotACrossing()
    {
        // 선분은 y 0~1 전체라 우회로가 없다 — 짧은 선분(y 0.2~0.8)으로 따로 만든다.
        var scenario = new EdgeScenarioConfig
        {
            ScenarioId = "wh", Type = "monitoring",
            Sources = [new StreamSource { Id = "cam1", CameraRef = "dock-1", Lines = [new StreamLine { Id = "door", A = [0.5, 0.2], B = [0.5, 0.8] }] }],
            Rules = [LineCount()],
        };
        var engine = new RuleEngine([scenario]);
        var events = new List<RuleEvent>();
        events.AddRange(engine.Process(Frame(0, 0, Box("f1", 0.7, 0.95, "forklift"))));
        events.AddRange(engine.Process(Frame(1, 1, Box("f1", 0.3, 0.95, "forklift"))));   // 선분 아래 연장선을 넘음

        Assert.Empty(events);
    }

    [Fact]
    public void Line_TracksAreIndependent_AndForgottenAfterTheyLeave()
    {
        var engine = new RuleEngine([Scenario([LineCount()])]);
        var events = new List<RuleEvent>();
        events.AddRange(engine.Process(Frame(0, 0, Box("a", 0.7, 0.3, "forklift"), Box("b", 0.3, 0.7, "forklift"))));
        events.AddRange(engine.Process(Frame(1, 1, Box("a", 0.3, 0.3, "forklift"), Box("b", 0.7, 0.7, "forklift"))));

        Assert.Equal(["in", "out"], events.OrderBy(e => Get<string>(e, "track_id")).Select(e => Get<string>(e, "direction")));

        // a 가 40 초 보이지 않다가(기억 30 초) 같은 id 로 반대쪽에 나타나도 이전 위치가 없으니 통과가 아니다.
        events.Clear();
        events.AddRange(engine.Process(Frame(2, 41, Box("a", 0.7, 0.3, "forklift"))));
        Assert.Empty(events);
    }

    // --- 출력 필터 · 건너뛰기 · 소스 ---

    [Fact]
    public void Outputs_OmittedMeansEverything_ListedMeansOnlyThose()
    {
        StreamRule[] rules = [ZoneCount(frames: 1), ZoneCount("stock2", frames: 1)];
        var frame = Frame(0, 0, Box("a", 0.3, 0.5));

        Assert.Equal(2, new RuleEngine([Scenario(rules)]).Process(frame).Count);
        Assert.Empty(new RuleEngine([Scenario(rules, [new StreamOutput { Event = "alarm_raised" }])]).Process(frame));
        var onlyOne = new RuleEngine([Scenario(rules, [new StreamOutput { Event = "count_changed", Rule = "stock2" }])]).Process(frame);
        Assert.Equal("stock2", Get<string>(Assert.Single(onlyOne), "rule"));
    }

    [Fact]
    public void FramesOfOtherSources_AndOtherScenarioTypes_AreIgnored()
    {
        var counting = new EdgeScenarioConfig { ScenarioId = "plain", Type = "counting", Rules = [ZoneCount(frames: 1)] };
        var engine = new RuleEngine([Scenario([ZoneCount(frames: 1)]), counting]);

        Assert.Equal(1, engine.RuleCount);
        Assert.Empty(engine.Process(new TrackFrame("cam2", 0, T0, [Box("a", 0.3, 0.5)])));
    }

    [Fact]
    public void RulesPointingAtMissingShapes_AreSkippedWithAReason_NotThrown()
    {
        var engine = new RuleEngine([Scenario([
            ZoneCount("ghost-zone", zone: "nope"),
            new StreamRule { Id = "ghost-line", Kind = "count", Source = "cam1", Line = "nope", Class = "box" },
            new StreamRule { Id = "ghost-src", Kind = "count", Source = "cam9", Class = "box" },
            ZoneCount("ok", frames: 1),
        ])]);

        Assert.Equal(1, engine.RuleCount);
        Assert.Equal(3, engine.Skipped.Count);
        Assert.Contains(engine.Skipped, s => s.Contains("ghost-zone") && s.Contains("nope"));
        Assert.Single(engine.Process(Frame(0, 0, Box("a", 0.3, 0.5))));
    }
}
