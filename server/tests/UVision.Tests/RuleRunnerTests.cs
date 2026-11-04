using System.Text.Json;
using UVision.Edge.Agent;
using UVision.Edge.Agent.Rules;
using UVision.Edge.Agent.Worker;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// <see cref="RuleRunner"/> — 워커가 건넨 것이 규칙을 거쳐 <b>사건만</b> 내구 outbox 에 남는지, 구성이 바뀔 때 올라간 경보가 잊히지 않는지,
/// 구성이 없을 때(엔진이 한 번도 안 닿았을 때) 조용히 아무 규칙도 돌리지 않는지.
/// </summary>
public sealed class RuleRunnerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uvr-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AgentOutbox _outbox;

    public RuleRunnerTests() => _outbox = AgentOutbox.Open(_dir);

    public void Dispose()
    {
        _outbox.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static EdgeConfig Config(string version, params EdgeScenarioConfig[] scenarios) =>
        new() { NodeId = "n1", Version = version, Scenarios = [.. scenarios] };

    private static EdgeScenarioConfig Watch(string id = "wh", string zoneRule = "stock", double forSeconds = 5) => new()
    {
        ScenarioId = id,
        Type = "monitoring",
        Sources = [new StreamSource { Id = "cam1", CameraRef = "dock-1", Zones = [new StreamZone { Id = "shelf", Polygon = [[0.1, 0.1], [0.5, 0.1], [0.5, 0.9], [0.1, 0.9]] }] }],
        Rules =
        [
            new StreamRule { Id = zoneRule, Kind = "count", Source = "cam1", Zone = "shelf", Class = "box", StableFrames = 2 },
            new StreamRule { Id = "intruder", Kind = "alarm", Source = "cam1", Zone = "shelf", Class = "person", MinCount = 1, ForSeconds = forSeconds },
        ],
    };

    private static TrackSample Box() => new("b", "box", 0.9, 0.25, 0.4, 0.1, 0.1);

    private static TrackSample Person() => new("p", "person", 0.9, 0.25, 0.3, 0.1, 0.2);

    private static TrackFrame Frame(double seconds, params TrackSample[] tracks) => new("dock-1", (long)seconds, T0.AddSeconds(seconds), tracks);

    private string[] Kinds() => [.. _outbox.Peek(1000).Select(i => i.EventKind!)];

    [Fact]
    public void Events_AreWrittenDurably_WithTheFrameTimeAndTheRulesData()
    {
        var runner = new RuleRunner(_outbox);
        runner.Configure(Config("v1", Watch()));

        runner.OnFrame(Frame(0, Box()));
        runner.OnFrame(Frame(1, Box()));   // 두 프레임 안정 → 기준선 사건

        var item = Assert.Single(_outbox.Peek(10));
        Assert.Equal(("wh", "count_changed"), (item.ScenarioId, item.EventKind));
        Assert.Equal(T0.AddSeconds(1), DateTimeOffset.Parse(item.DeviceTime));
        Assert.Equal(1, item.Data!.Value.GetProperty("count").GetInt32());
        Assert.Equal(1, runner.EventsEnqueued);

    }

    [Fact]
    public void Events_SurviveARestart_BecauseTheyAreCompleteOnDiskWhenEnqueueReturns()
    {
        var durable = _dir + "-durable";
        try
        {
            using (var box = AgentOutbox.Open(durable))
            {
                var runner = new RuleRunner(box);
                runner.Configure(Config("v1", Watch()));
                runner.OnFrame(Frame(0, Box()));
                runner.OnFrame(Frame(1, Box()));
            }   // 프로세스가 죽은 것과 같다 — 닫고 다시 연다

            using var reopened = AgentOutbox.Open(durable);
            Assert.Equal("count_changed", Assert.Single(reopened.Peek(10)).EventKind);
        }
        finally
        {
            try { Directory.Delete(durable, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void WithoutAConfig_NothingRuns_AndNothingIsWritten()
    {
        var runner = new RuleRunner(_outbox);
        runner.Configure(null);

        runner.OnFrame(Frame(0, Box()));
        runner.OnFrame(Frame(1, Box()));
        runner.OnWorkerLost(T0.AddSeconds(2));

        Assert.Equal(0, runner.RuleCount);
        Assert.Empty(_outbox.Peek(10));
    }

    [Fact]
    public void ASameVersionConfig_IsANoOp_ItDoesNotResetState()
    {
        var runner = new RuleRunner(_outbox);
        runner.Configure(Config("v1", Watch()));
        runner.OnFrame(Frame(0, Box()));
        runner.Configure(Config("v1", Watch()));      // 폴링이 같은 판을 다시 줘도
        runner.OnFrame(Frame(1, Box()));              // 안정 구간이 이어진다 → 기준선

        Assert.Equal(["count_changed"], Kinds());
    }

    [Fact]
    public void ARaisedAlarmSurvivesAConfigChange_WhenItsScenarioIsUnchanged()
    {
        var runner = new RuleRunner(_outbox);
        runner.Configure(Config("v1", Watch(), Watch("other")));
        for (var s = 0; s <= 5; s++) runner.OnFrame(Frame(s, Person()));   // 5 초 → alarm_raised (두 시나리오)
        Assert.Equal(2, Kinds().Count(k => k == "alarm_raised"));

        // 다른 시나리오가 추가·변경돼 판이 바뀌어도, 소스·규칙이 그대로인 시나리오의 경보는 이어진다.
        runner.Configure(Config("v2", Watch(), Watch("other", forSeconds: 99)));
        for (var s = 6; s <= 12; s++) runner.OnFrame(Frame(s, Person()));

        // 변하지 않은 "wh" 는 다시 올리지 않는다; 바뀐 "other" 는 처음부터 — 99 초 전이라 아직 안 올린다.
        Assert.Equal(2, Kinds().Count(k => k == "alarm_raised"));
        Assert.Equal(["wh", "other"], _outbox.Peek(100).Where(i => i.EventKind == "alarm_raised").Select(i => i.ScenarioId));
    }

    [Fact]
    public void AChangedScenarioStartsOver_AndAnnouncesItsBaselineAgain()
    {
        var runner = new RuleRunner(_outbox);
        runner.Configure(Config("v1", Watch()));
        runner.OnFrame(Frame(0, Box()));
        runner.OnFrame(Frame(1, Box()));
        Assert.Equal(1, Kinds().Count(k => k == "count_changed"));

        runner.Configure(Config("v2", Watch(zoneRule: "stock-renamed")));   // 규칙이 바뀜
        runner.OnFrame(Frame(2, Box()));
        runner.OnFrame(Frame(3, Box()));

        Assert.Equal(2, Kinds().Count(k => k == "count_changed"));          // 새 규칙의 기준선
    }

    [Fact]
    public void CameraLossCarriesOverAConfigChange_WithoutBeingAnnouncedAgain()
    {
        var runner = new RuleRunner(_outbox);
        runner.Configure(Config("v1", Watch()));
        runner.OnWorkerLost(T0);
        Assert.Equal(["source_state"], Kinds());

        runner.Configure(Config("v2", Watch(), Watch("other")));            // 판이 바뀌어도 이미 소실인 카메라를 다시 알리지 않는다
        runner.OnWorkerLost(T0.AddSeconds(1));

        Assert.Equal(["source_state"], Kinds());
        Assert.Equal(["wh"], _outbox.Peek(10).Select(i => i.ScenarioId));
    }

    [Fact]
    public void SkippedRulesAreVisible_AndTheValidOnesStillRun()
    {
        var broken = Watch() with
        {
            Rules = [.. Watch().Rules!, new StreamRule { Id = "ghost", Kind = "count", Source = "nope", Class = "box" }],
        };
        var runner = new RuleRunner(_outbox);
        runner.Configure(Config("v1", broken));

        Assert.Equal(2, runner.RuleCount);
        Assert.Contains(runner.Skipped, s => s.Contains("ghost"));
        Assert.Equal(["dock-1"], runner.CameraRefs);
    }
}
