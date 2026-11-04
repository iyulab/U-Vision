using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 스트림 감시 시나리오 어휘(type: monitoring · trigger: stream — 원장 D-105 W-4 · W-5): 경계가 지어진 값만 받는지, 카메라 주소가 시나리오에
/// 닿지 않는지, 수정이 구성을 지우지 않는지, 사진 판정 길(/inspect)로 부를 수 없는지.
/// </summary>
public class StreamScenarioTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;

    public StreamScenarioTests(PinFactory factory) => _factory = factory;

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        return client;
    }

    private static List<List<double>> Square() => [[0.1, 0.1], [0.9, 0.1], [0.9, 0.9], [0.1, 0.9]];

    /// <summary>검증을 통과하는 가장 흔한 모양 — 구역 하나를 세는 규칙 + 부재 경보 + 선 통과 세기.</summary>
    private static ScenarioInput Valid() => new()
    {
        Name = "재고 창고",
        Type = "monitoring",
        Sources =
        [
            new ScenarioSource
            {
                Id = "cam1", CameraRef = "dock-1",
                Zones = [new ScenarioZone { Id = "shelf", Polygon = Square() }],
                Lines = [new ScenarioLine { Id = "door", A = [0.5, 0.0], B = [0.5, 1.0] }],
            },
        ],
        Rules =
        [
            new ScenarioRule { Id = "stock", Kind = "count", Source = "cam1", Zone = "shelf", Class = "box" },
            new ScenarioRule { Id = "traffic", Kind = "count", Source = "cam1", Line = "door", Class = "forklift" },
            new ScenarioRule { Id = "empty", Kind = "alarm", Source = "cam1", Zone = "shelf", Class = "box", MaxCount = 0, ForSeconds = 30 },
        ],
    };

    [Fact]
    public void Valid_PassesTheBoundary()
    {
        Assert.Null(EffectiveScenario.Validate(Valid()));
    }

    [Fact]
    public void Resolve_Monitoring_IsAStreamScenarioWithoutJudgeTiers()
    {
        var scenario = Valid().ToScenario("watch");
        var e = EffectiveScenario.Resolve(scenario, mlEnabled: true, memoryEnabled: true);

        Assert.True(e.IsMonitoring);
        Assert.Equal("stream", e.Trigger);
        Assert.Empty(e.Tiers); // 켜진 ML · 기억 단이 있어도 판정 라우터를 타지 않는다
    }

    [Theory]
    [InlineData("rtsp://user:secret@10.0.0.5/stream")]   // 비밀번호가 시나리오에 닿으면 안 된다(W-5)
    [InlineData("10.0.0.5:554")]
    [InlineData("a/b")]
    [InlineData("user@host")]
    [InlineData("has space")]
    [InlineData("")]
    public void CameraRef_IsAnAlias_NeverAnAddress(string cameraRef)
    {
        var input = Valid();
        var bad = input with { Sources = [input.Sources![0] with { CameraRef = cameraRef }] };

        var error = EffectiveScenario.Validate(bad);

        Assert.NotNull(error);
        Assert.Contains("camera_ref", error);
        Assert.DoesNotContain("secret", error); // 거부 사유도 값을 되풀이하지 않는다
    }

    [Fact]
    public void StreamFields_AreOnlyForMonitoring()
    {
        var v = Valid();
        var inspection = new ScenarioInput { Name = "x", Sources = v.Sources };

        Assert.Contains("monitoring", EffectiveScenario.Validate(inspection));
        Assert.Contains("monitoring", EffectiveScenario.Validate(new ScenarioInput { Name = "x", Trigger = "stream" }));
    }

    [Fact]
    public void Monitoring_RequiresSourcesAndRules()
    {
        Assert.Contains("sources", EffectiveScenario.Validate(new ScenarioInput { Name = "x", Type = "monitoring" }));
        Assert.Contains("rules", EffectiveScenario.Validate(Valid() with { Rules = null }));
        Assert.Contains("rules", EffectiveScenario.Validate(Valid() with { Rules = [] }));
    }

    [Fact]
    public void Monitoring_RejectsTheJudgeRouterFields()
    {
        Assert.Contains("판정 라우터", EffectiveScenario.Validate(Valid() with { Judges = [new ScenarioJudge { Tier = "vlm" }] }));
        Assert.Contains("판정 라우터", EffectiveScenario.Validate(Valid() with { Classes = [new ScenarioClass { Id = "OK", Settleable = true }] }));
    }

    [Fact]
    public void Monitoring_TriggerIsStreamOrNothing()
    {
        Assert.Null(EffectiveScenario.Validate(Valid() with { Trigger = "stream" }));
        Assert.Contains("stream", EffectiveScenario.Validate(Valid() with { Trigger = "still" }));
    }

    [Fact]
    public void Rules_MustPointAtRealSourcesZonesAndLines()
    {
        Rule(r => r with { Source = "nope" }, "source");
        Rule(r => r with { Zone = "nope" }, "zone");
        Rule(r => r with { Zone = null, Line = "nope" }, "line");
        Rule(r => r with { Zone = "shelf", Line = "door" }, "zone 과 line");
        Rule(r => r with { Kind = "average" }, "kind");
        Rule(r => r with { Class = "" }, "class");
        Rule(r => r with { Id = "a b" }, "id");
    }

    [Fact]
    public void CountRule_StabilityIsFramesOrSeconds_NotBoth()
    {
        Rule(r => r with { StableFrames = 3, StableSeconds = 1 }, "함께");
        Rule(r => r with { StableFrames = 0 }, "stable_frames");
        Rule(r => r with { StableSeconds = 0 }, "stable_seconds");
        Rule(r => r with { ForSeconds = 5 }, "alarm");              // alarm 의 필드
        Rule(r => r with { Zone = null, Line = "door", StableFrames = 3 }, "순간");   // 선 통과에는 안정 구간이 없다
        Assert.Null(EffectiveScenario.Validate(WithRule("stock", r => r with { StableSeconds = 2.5 })));
        Assert.Null(EffectiveScenario.Validate(WithRule("stock", r => r with { StableFrames = 10 })));
    }

    [Fact]
    public void AlarmRule_NeedsExactlyOneCondition_AndADuration()
    {
        Alarm(r => r with { MinCount = null, MaxCount = null }, "정확히 하나");
        Alarm(r => r with { MinCount = 1, MaxCount = 0 }, "정확히 하나");
        Alarm(r => r with { MaxCount = null, MinCount = 0 }, "min_count");   // 0 이상은 항상 참
        Alarm(r => r with { ForSeconds = null }, "for_seconds");
        Alarm(r => r with { ForSeconds = 0 }, "for_seconds");
        Alarm(r => r with { ClearSeconds = -1 }, "clear_seconds");
        Alarm(r => r with { Zone = null, Line = "door" }, "line");
        Alarm(r => r with { StableFrames = 3 }, "stable_frames");
        Assert.Null(EffectiveScenario.Validate(WithRule("empty", r => r with { MaxCount = null, MinCount = 1, ClearSeconds = 10 })));
    }

    [Fact]
    public void Shapes_AreNormalizedPointsWithRealArea()
    {
        Shape(z => z with { Polygon = [[0, 0], [1, 1]] }, "polygon");                         // 꼭짓점 둘
        Shape(z => z with { Polygon = [[0, 0], [1, 1], [2, 2]] }, "polygon");                 // 범위 밖
        Shape(z => z with { Polygon = [[0, 0], [0.5, 0.5], [1, 1]] }, "넓이");                 // 한 줄 위
        Shape(z => z with { Polygon = [[0, 0], [1, 0], [double.NaN, 1]] }, "polygon");
    }

    [Fact]
    public void Outputs_NameOnlyEventsTheRulesCanEmit()
    {
        Assert.Null(EffectiveScenario.Validate(Valid() with
        {
            Outputs = [new ScenarioOutput { Event = "count_changed" }, new ScenarioOutput { Event = "alarm_raised", Rule = "empty" }, new ScenarioOutput { Event = "source_state" }],
        }));
        Assert.Contains("event", EffectiveScenario.Validate(Valid() with { Outputs = [new ScenarioOutput { Event = "boom" }] }));
        Assert.Contains("내지 않는다", EffectiveScenario.Validate(Valid() with { Outputs = [new ScenarioOutput { Event = "alarm_raised", Rule = "stock" }] }));
        Assert.Contains("rules 에 없다", EffectiveScenario.Validate(Valid() with { Outputs = [new ScenarioOutput { Event = "count_changed", Rule = "ghost" }] }));
        Assert.Contains("소스가 내므로", EffectiveScenario.Validate(Valid() with { Outputs = [new ScenarioOutput { Event = "source_state", Rule = "stock" }] }));
        Assert.Contains("겹친다", EffectiveScenario.Validate(Valid() with { Outputs = [new ScenarioOutput { Event = "count_changed" }, new ScenarioOutput { Event = "count_changed" }] }));
        Assert.Contains("비었다", EffectiveScenario.Validate(Valid() with { Outputs = [] }));
    }

    [Fact]
    public void Update_OmittingStreamFieldsKeepsThem_AndChangingTypeDropsThem()
    {
        var existing = Valid().ToScenario("watch") with { Trigger = "stream" };

        // 이름만 고치는 수정(stream 필드 생략)은 합친 모양으로 검증된다.
        Assert.Null(EffectiveScenario.Validate(new ScenarioInput { Name = "새 이름" }, existing));

        // 유형을 바꾸면 stream 필드는 옮기지 않으므로 trigger stream 도 함께 놓인다 — 검사로 돌아간다.
        Assert.Null(EffectiveScenario.Validate(new ScenarioInput { Name = "x", Type = "inspection" }, existing));
        // 같은 요청에 유형 검사 + stream 필드를 주면 거부한다.
        Assert.Contains("monitoring", EffectiveScenario.Validate(new ScenarioInput { Name = "x", Type = "inspection", Rules = Valid().Rules }, existing));
    }

    [Fact]
    public async Task Endpoint_RoundTripsTheVocabulary_AndRejectsInspect()
    {
        var admin = Admin();
        var input = Valid() with { ScenarioId = "watch-wh" };

        var created = await admin.PostAsJsonAsync("/api/u-vision/scenarios", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var read = await (await admin.GetAsync("/api/u-vision/scenarios/watch-wh")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("monitoring", read.GetProperty("type").GetString());
        Assert.Equal("dock-1", read.GetProperty("sources")[0].GetProperty("camera_ref").GetString());
        Assert.Equal(3, read.GetProperty("rules").GetArrayLength());
        Assert.Equal("alarm", read.GetProperty("rules")[2].GetProperty("kind").GetString());

        // 이름만 고쳐도 sources · rules 가 남는다(생략 = 보존).
        var renamed = await admin.PutAsJsonAsync("/api/u-vision/scenarios/watch-wh", new { name = "새 이름" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var after = await (await admin.GetAsync("/api/u-vision/scenarios/watch-wh")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, after.GetProperty("rules").GetArrayLength());
        Assert.Equal("monitoring", after.GetProperty("type").GetString()); // trigger 는 저장하지 않아도 유형에서 파생된다(stream)

        // 사진 판정 길로는 부를 수 없다 — 판정 레코드를 만들 길이 없다.
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]) { Headers = { ContentType = new("image/jpeg") } }, "image", "a.jpg" },
            { new StringContent("watch-wh"), "scenario_id" },
        };
        var inspect = await admin.PostAsync("/api/u-vision/inspect", form);
        Assert.Equal(HttpStatusCode.BadRequest, inspect.StatusCode);
        Assert.Contains("monitoring", await inspect.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Endpoint_RejectsAnAddressAsCameraRef_WithAnEmptyEcho()
    {
        var bad = Valid() with
        {
            ScenarioId = "leaky",
            Sources = [Valid().Sources![0] with { CameraRef = "rtsp://admin:hunter2@192.168.0.9/stream" }],
        };

        var resp = await Admin().PostAsJsonAsync("/api/u-vision/scenarios", bad);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.DoesNotContain("hunter2", await resp.Content.ReadAsStringAsync());
    }

    // --- 도우미 ---

    private static ScenarioInput WithRule(string id, Func<ScenarioRule, ScenarioRule> change)
    {
        var v = Valid();
        return v with { Rules = [.. v.Rules!.Select(r => r.Id == id ? change(r) : r)] };
    }

    private static void Rule(Func<ScenarioRule, ScenarioRule> change, string expectedFragment)
    {
        var error = EffectiveScenario.Validate(WithRule("stock", change));
        Assert.True(error is not null && error.Contains(expectedFragment), $"기대 조각 «{expectedFragment}» — 실제: {error ?? "(통과)"}");
    }

    private static void Alarm(Func<ScenarioRule, ScenarioRule> change, string expectedFragment)
    {
        var error = EffectiveScenario.Validate(WithRule("empty", change));
        Assert.True(error is not null && error.Contains(expectedFragment), $"기대 조각 «{expectedFragment}» — 실제: {error ?? "(통과)"}");
    }

    private static void Shape(Func<ScenarioZone, ScenarioZone> change, string expectedFragment)
    {
        var v = Valid();
        var source = v.Sources![0];
        var bad = v with { Sources = [source with { Zones = [change(source.Zones![0])] }] };
        var error = EffectiveScenario.Validate(bad);
        Assert.True(error is not null && error.Contains(expectedFragment), $"기대 조각 «{expectedFragment}» — 실제: {error ?? "(통과)"}");
    }
}
