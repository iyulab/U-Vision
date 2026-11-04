using System.Text.Json;
using UVision.Edge.Agent;
using UVision.Edge.Agent.Rules;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 규칙 평가기의 <c>alarm</c> 과 소스 상태(원장 D-105 W-4 · W-6) — 지속 시간으로 올리고 내리는 히스테리시스, 프레임이 없으면 평가도 없다는 것,
/// 소스 소실이 침묵이 아니라 사건이라는 것.
/// </summary>
public class RuleEngineAlarmTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    private static List<List<double>> Shelf() => [[0.1, 0.1], [0.5, 0.1], [0.5, 0.9], [0.1, 0.9]];

    private static EdgeScenarioConfig Scenario(
        IEnumerable<StreamRule> rules, List<StreamOutput>? outputs = null, string id = "wh", string camera = "dock-1") => new()
    {
        ScenarioId = id,
        Type = "monitoring",
        Sources = [new StreamSource { Id = "cam1", CameraRef = camera, Zones = [new StreamZone { Id = "shelf", Polygon = Shelf() }] }],
        Rules = [.. rules],
        Outputs = outputs,
    };

    // 존재 경보: 구역에 사람이 10 초 있으면 올라간다.
    private static StreamRule Presence(double forSeconds = 10, double? clearSeconds = null, string? zone = "shelf") => new()
    {
        Id = "intruder", Kind = "alarm", Source = "cam1", Zone = zone, Class = "person", MinCount = 1, ForSeconds = forSeconds, ClearSeconds = clearSeconds,
    };

    // 부재 경보: 선반에 상자가 30 초 없으면 올라간다.
    private static StreamRule Absence(double forSeconds = 30) => new()
    {
        Id = "empty", Kind = "alarm", Source = "cam1", Zone = "shelf", Class = "box", MaxCount = 0, ForSeconds = forSeconds,
    };

    private static TrackSample Person(double gx = 0.3, double gy = 0.5) => new("p", "person", 0.9, gx - 0.05, gy - 0.2, 0.1, 0.2);

    private static TrackSample Box(double gx = 0.3, double gy = 0.5) => new("b", "box", 0.9, gx - 0.05, gy - 0.1, 0.1, 0.1);

    private static TrackFrame Frame(double seconds, params TrackSample[] tracks) => new("dock-1", (long)seconds, T0.AddSeconds(seconds), tracks);

    private static T Get<T>(RuleEvent e, string key) => e.Data.GetProperty(key).Deserialize<T>()!;

    private static List<RuleEvent> Run(RuleEngine engine, IEnumerable<TrackFrame> frames) => [.. frames.SelectMany(engine.Process)];

    // --- alarm ---

    [Fact]
    public void Presence_RaisesOnlyAfterTheConditionHeldForTheDuration()
    {
        var engine = new RuleEngine([Scenario([Presence(forSeconds: 10)])]);

        var events = Run(engine, Enumerable.Range(0, 10).Select(s => Frame(s, Person())));   // t=0..9 — 9 초 지속
        Assert.Empty(events);

        var raised = Assert.Single(engine.Process(Frame(10, Person())));                     // t=10 — 10 초 채움
        Assert.Equal("alarm_raised", raised.Kind);
        Assert.Equal(T0.AddSeconds(10), raised.Time);
        Assert.Equal(T0, Get<DateTimeOffset>(raised, "since"));                               // 조건이 참이 되기 시작한 시각
        Assert.Equal((1, 1), (Get<int>(raised, "count"), Get<int>(raised, "min_count")));
        Assert.Equal("shelf", Get<string>(raised, "zone"));
        Assert.Equal(JsonValueKind.Null, raised.Data.GetProperty("max_count").ValueKind);
    }

    [Fact]
    public void Presence_AnInterruptionRestartsTheCountdown_AndRaisesOnlyOnce()
    {
        var engine = new RuleEngine([Scenario([Presence(forSeconds: 10)])]);
        var events = new List<RuleEvent>();
        events.AddRange(Run(engine, Enumerable.Range(0, 8).Select(s => Frame(s, Person()))));   // 0..7 있음
        events.AddRange(engine.Process(Frame(8)));                                               // 한 번 비면 카운트다운이 처음부터
        events.AddRange(Run(engine, Enumerable.Range(9, 9).Select(s => Frame(s, Person()))));    // 9..17 — 9 초
        Assert.Empty(events);

        events.AddRange(engine.Process(Frame(18, Person())));                                    // 9 초에서 시작해 10 초 → 18 이 아니라 19
        events.AddRange(engine.Process(Frame(19, Person())));
        Assert.Equal(T0.AddSeconds(19), Assert.Single(events).Time);

        // 계속 있어도 다시 올리지 않는다.
        Assert.Empty(Run(engine, Enumerable.Range(20, 30).Select(s => Frame(s, Person()))));
    }

    [Fact]
    public void Presence_ClearsOnlyAfterTheOppositeStateHeldForClearSeconds_DefaultingToForSeconds()
    {
        var engine = new RuleEngine([Scenario([Presence(forSeconds: 5, clearSeconds: 8)])]);
        Run(engine, Enumerable.Range(0, 6).Select(s => Frame(s, Person())));                    // 5 초에 올라감

        // 떠난 뒤 7 초 — 8 초 전이라 아직 올라간 채.
        Assert.Empty(Run(engine, Enumerable.Range(6, 8).Select(s => Frame(s))));                // t=6..13: 비어 있음 7 초
        // 잠깐 돌아오면 내림 카운트다운이 처음부터.
        Assert.Empty(engine.Process(Frame(14, Person())));
        Assert.Empty(Run(engine, Enumerable.Range(15, 8).Select(s => Frame(s))));               // t=15..22: 7 초
        var cleared = Assert.Single(engine.Process(Frame(23)));                                 // t=23: 8 초
        Assert.Equal("alarm_cleared", cleared.Kind);
        Assert.Equal(T0.AddSeconds(5), Get<DateTimeOffset>(cleared, "raised_at"));
        Assert.Equal(T0.AddSeconds(15), Get<DateTimeOffset>(cleared, "since"));                 // 조건이 깨지기 시작한 시각

        // 기본값: clear_seconds 를 안 주면 for_seconds 와 같다 — 두 번째 경보가 다시 올라갈 수 있다.
        var defaults = new RuleEngine([Scenario([Presence(forSeconds: 3)])]);
        Run(defaults, Enumerable.Range(0, 4).Select(s => Frame(s, Person())));
        Assert.Empty(Run(defaults, Enumerable.Range(4, 3).Select(s => Frame(s))));              // 비어 있음 2 초
        Assert.Equal("alarm_cleared", Assert.Single(defaults.Process(Frame(7))).Kind);           // 3 초
        Run(defaults, Enumerable.Range(8, 3).Select(s => Frame(s, Person())));
        Assert.Equal("alarm_raised", Assert.Single(defaults.Process(Frame(11, Person()))).Kind);
    }

    [Fact]
    public void Absence_RaisesWhenTheCountStaysAtOrBelowTheMaximum()
    {
        var engine = new RuleEngine([Scenario([Absence(forSeconds: 30)])]);

        // 상자가 있다가(0..9) 사라진 채 30 초.
        Assert.Empty(Run(engine, Enumerable.Range(0, 10).Select(s => Frame(s, Box()))));
        Assert.Empty(Run(engine, Enumerable.Range(10, 30).Select(s => Frame(s))));              // t=10..39 — 29 초
        var raised = Assert.Single(engine.Process(Frame(40)));                                  // 30 초
        Assert.Equal("alarm_raised", raised.Kind);
        Assert.Equal(T0.AddSeconds(10), Get<DateTimeOffset>(raised, "since"));
        Assert.Equal((0, 0), (Get<int>(raised, "count"), Get<int>(raised, "max_count")));

        // 상자가 돌아와도 30 초 지속해야 내려간다.
        Assert.Empty(Run(engine, Enumerable.Range(41, 30).Select(s => Frame(s, Box()))));       // t=41..70 — 29 초
        Assert.Equal("alarm_cleared", Assert.Single(engine.Process(Frame(71, Box()))).Kind);    // 30 초
    }

    [Fact]
    public void NoFrames_MeansNoEvaluation_AnAbsenceAlarmDoesNotRiseOnSilence()
    {
        var engine = new RuleEngine([Scenario([Absence(forSeconds: 30)])]);
        Run(engine, Enumerable.Range(0, 5).Select(s => Frame(s, Box())));

        // 한 시간이 지나도 프레임이 없으면 평가가 없다 — 소스 소실은 source_state 가 알린다. 다음 프레임이 와야 시간이 흐른다.
        var next = engine.Process(Frame(3600));
        Assert.Empty(next);                                                                     // 이제 막 «없음» 이 시작된 프레임(상자가 사라진 첫 프레임)
    }

    [Fact]
    public void Alarm_WithoutAZone_WatchesTheWholeFrame()
    {
        var engine = new RuleEngine([Scenario([Presence(forSeconds: 2, zone: null)])]);
        var events = Run(engine, [Frame(0, Person(0.9, 0.9)), Frame(1, Person(0.9, 0.9)), Frame(2, Person(0.9, 0.9))]);   // 선반 밖이지만 프레임 안

        Assert.Equal("alarm_raised", Assert.Single(events).Kind);
        Assert.Equal(JsonValueKind.Null, events[0].Data.GetProperty("zone").ValueKind);
    }

    [Fact]
    public void Alarm_OutsideTheZoneDoesNotCount()
    {
        var engine = new RuleEngine([Scenario([Presence(forSeconds: 2)])]);
        Assert.Empty(Run(engine, Enumerable.Range(0, 10).Select(s => Frame(s, Person(0.9, 0.9)))));
    }

    [Fact]
    public void AlarmRulesMissingTheirParameters_AreSkippedNotCrashed()
    {
        var engine = new RuleEngine([Scenario([
            Presence() with { ForSeconds = null, Id = "no-duration" },
            Presence() with { MinCount = null, MaxCount = null, Id = "no-condition" },
            Presence() with { MaxCount = 0, Id = "both-conditions" },
            Absence(),
        ])]);

        Assert.Equal(1, engine.RuleCount);
        Assert.Equal(3, engine.Skipped.Count);
    }

    // --- 소스 상태 ---

    [Fact]
    public void SourceState_ALossIsAnEvent_ForEveryScenarioThatWatchesThatCamera()
    {
        var engine = new RuleEngine([
            Scenario([Presence()], id: "wh"),
            Scenario([Absence()], id: "audit"),
            Scenario([Absence()], id: "elsewhere", camera: "dock-9"),
        ]);

        var events = engine.OnSourceState(new SourceStateMessage("dock-1", "no_signal", T0.AddSeconds(5)));

        Assert.Equal(["audit", "wh"], events.Select(e => e.ScenarioId).Order());
        Assert.All(events, e => Assert.Equal("source_state", e.Kind));
        var e0 = events[0];
        Assert.Equal(("no_signal", "ok", "dock-1", "cam1"), (Get<string>(e0, "state"), Get<string>(e0, "previous"), Get<string>(e0, "camera"), Get<string>(e0, "source")));
        Assert.Equal(T0.AddSeconds(5), e0.Time);
    }

    [Fact]
    public void SourceState_OnlyChangesAreEvents_AndUnknownThingsAreIgnored()
    {
        var engine = new RuleEngine([Scenario([Presence()])]);

        Assert.Empty(engine.OnSourceState(new("dock-1", "ok", T0)));                        // 처음 상태는 ok — 변화 없음
        Assert.Single(engine.OnSourceState(new("dock-1", "degraded", T0.AddSeconds(1))));
        Assert.Empty(engine.OnSourceState(new("dock-1", "degraded", T0.AddSeconds(2))));    // 반복
        Assert.Empty(engine.OnSourceState(new("dock-1", "exploded", T0.AddSeconds(3))));    // 모르는 상태
        Assert.Empty(engine.OnSourceState(new("nobody", "no_signal", T0.AddSeconds(4))));   // 아무 시나리오도 안 보는 카메라
        var back = Assert.Single(engine.OnSourceState(new("dock-1", "ok", T0.AddSeconds(5))));
        Assert.Equal(("ok", "degraded"), (Get<string>(back, "state"), Get<string>(back, "previous")));
    }

    [Fact]
    public void SourceState_AFrameArrivingAfterALossRestoresIt()
    {
        var engine = new RuleEngine([Scenario([Presence(forSeconds: 100)])]);
        engine.OnSourceState(new("dock-1", "no_signal", T0));

        var events = engine.Process(Frame(30, Person()));

        var restored = Assert.Single(events);
        Assert.Equal(("source_state", "ok", "no_signal"), (restored.Kind, Get<string>(restored, "state"), Get<string>(restored, "previous")));
        Assert.Equal(T0.AddSeconds(30), restored.Time);
        Assert.Empty(engine.Process(Frame(31, Person())));                                   // 한 번만
    }

    [Fact]
    public void SourceState_DegradedIsNotRestoredByFrames_OnlyByTheWorker()
    {
        var engine = new RuleEngine([Scenario([Presence(forSeconds: 100)])]);
        engine.OnSourceState(new("dock-1", "degraded", T0));

        Assert.Empty(engine.Process(Frame(1, Person())));                                    // 보이지만 품질이 나쁜 상태 — 프레임이 풀지 않는다
    }

    [Fact]
    public void WorkerLost_MarksEveryKnownCameraAsLost_OnceEach()
    {
        var engine = new RuleEngine([Scenario([Presence()]), Scenario([Absence()], id: "other", camera: "dock-2")]);

        var events = engine.OnWorkerLost(T0.AddSeconds(9));

        Assert.Equal(["dock-1", "dock-2"], events.Select(e => Get<string>(e, "camera")).Order());
        Assert.Empty(engine.OnWorkerLost(T0.AddSeconds(10)));                                // 이미 소실이면 반복하지 않는다
    }

    [Fact]
    public void Outputs_CanSilenceSourceState_AndAlarmClears()
    {
        var onlyRaised = new List<StreamOutput> { new() { Event = "alarm_raised" } };
        var engine = new RuleEngine([Scenario([Presence(forSeconds: 2)], onlyRaised)]);

        Assert.Empty(engine.OnSourceState(new("dock-1", "no_signal", T0)));                 // source_state 는 목록에 없다 → 안 나간다
        var events = Run(engine, Enumerable.Range(1, 3).Select(s => Frame(s, Person())));
        Assert.Equal("alarm_raised", Assert.Single(events).Kind);
        Assert.Empty(Run(engine, Enumerable.Range(4, 5).Select(s => Frame(s))));            // alarm_cleared 는 걸러진다

        // 켜 두면 나간다.
        var withSource = new RuleEngine([Scenario([Presence()], [new() { Event = "source_state" }])]);
        Assert.Single(withSource.OnSourceState(new("dock-1", "no_signal", T0)));
    }
}
