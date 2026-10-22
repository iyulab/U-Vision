using System.Net;
using System.Net.Http.Json;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// NG 하위 유형 — 검사 시나리오가 <c>classes</c> 에 OK 와 불량 유형 여럿을 선언하면 다중분류가 되고, 선언하지 않으면
/// 지금의 OK/NG 그대로다. 유형은 모두 NG 쪽(v1 보기)이고 꼭 잡을 값이다.
/// </summary>
public class DefectClassTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;

    public DefectClassTests(PinFactory factory) => _factory = factory;

    private static readonly List<ScenarioClass> Typed =
    [
        new() { Id = "OK", Settleable = true },
        new() { Id = "scratch", MustCatch = true },
        new() { Id = "dent", MustCatch = true },
    ];

    private static EffectiveScenario Resolve(List<ScenarioClass>? classes = null, string? type = null) =>
        EffectiveScenario.Resolve(new Scenario { ScenarioId = "s", Name = "s", Classes = classes, Type = type }, mlEnabled: false);

    [Fact]
    public void Binary_IsNotMultiClass_AndHasTheSingleDefectNg()
    {
        var e = Resolve();
        Assert.False(e.IsMultiClass);
        Assert.Equal(["NG"], e.DefectClasses);
    }

    [Fact]
    public void Typed_IsMultiClass_WithItsDefectTypes()
    {
        var e = Resolve(Typed);
        Assert.True(e.IsMultiClass);
        Assert.Equal(["scratch", "dent"], e.DefectClasses);
    }

    [Fact]
    public void Verification_HasNoDefectTypes()
    {
        var e = Resolve(type: "verification");
        Assert.False(e.IsMultiClass);
        Assert.Empty(e.DefectClasses);
    }

    [Theory]
    [InlineData("OK", Verdict.OK)]
    [InlineData("scratch", Verdict.NG)]
    [InlineData("dent", Verdict.NG)]
    public void VerdictOf_EveryDefectTypeIsNg(string classId, Verdict expected)
    {
        Assert.Equal(expected, Resolve(Typed).VerdictOf(classId));
    }

    [Fact]
    public void VerdictOf_Verification_MatchIsOk()
    {
        var e = Resolve(type: "verification");
        Assert.Equal(Verdict.OK, e.VerdictOf("MATCH"));
        Assert.Equal(Verdict.NG, e.VerdictOf("MISMATCH"));
    }

    [Theory]
    [InlineData("SCRATCH", "scratch")]
    [InlineData("ok", "OK")]
    [InlineData("rust", null)]
    public void ClassFor_LabelsResolveToDeclaredIdsIgnoringCase(string label, string? expected)
    {
        Assert.Equal(expected, Resolve(Typed).ClassFor(label));
    }

    [Fact]
    public void ClassFor_Binary_AcceptsMlLowercaseLabels()
    {
        Assert.Equal("NG", Resolve().ClassFor("ng"));
        Assert.Null(Resolve().ClassFor("maybe"));
    }

    [Fact]
    public void ClassOf_Binary_IgnoresAnyDefectClass()
    {
        var r = new InspectionResult { Verdict = Verdict.NG, Confidence = 0.9, DefectClass = "scratch" };
        Assert.Equal("NG", Resolve().ClassOf(r));
    }

    [Fact]
    public void ClassOf_Typed_TakesTheNamedDefect()
    {
        var e = Resolve(Typed);
        Assert.Equal("dent", e.ClassOf(new InspectionResult { Verdict = Verdict.NG, Confidence = 0.9, DefectClass = "Dent" }));
        Assert.Equal("OK", e.ClassOf(new InspectionResult { Verdict = Verdict.OK, Confidence = 0.9, DefectClass = "dent" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("rust")]
    [InlineData("OK")]
    public void ClassOf_Typed_NgWithoutADeclaredDefectIsNoAnswer(string? defect)
    {
        var r = new InspectionResult { Verdict = Verdict.NG, Confidence = 0.9, DefectClass = defect };
        Assert.Null(Resolve(Typed).ClassOf(r));
    }

    [Fact]
    public void Labels_TypedUsesClassIds_BinaryAndVerificationUseOkNg()
    {
        Assert.Equal(["OK", "scratch", "dent"], Resolve(Typed).Labels);
        Assert.Equal(["OK", "NG"], Resolve().Labels);
        Assert.Equal(["OK", "NG"], Resolve(type: "verification").Labels);
    }

    public static TheoryData<string, string> Invalid => new()
    {
        { """{"name":"x","classes":[{"id":"scratch","must_catch":true},{"id":"dent","must_catch":true}]}""", "OK" },
        { """{"name":"x","classes":[{"id":"OK","settleable":true}]}""", "불량 유형" },
        { """{"name":"x","classes":[{"id":"OK","settleable":true},{"id":"scratch","must_catch":true},{"id":"dent"}]}""", "must_catch" },
        { """{"name":"x","classes":[{"id":"OK","must_catch":true},{"id":"NG","must_catch":true}]}""", "OK" },
        { """{"name":"x","classes":[{"id":"OK","settleable":true},{"id":"NG:scratch","must_catch":true}]}""", "영숫자" },
        { """{"name":"x","type":"verification","classes":[{"id":"MATCH","settleable":true},{"id":"MISMATCH","must_catch":true},{"id":"other","must_catch":true}]}""", "MISMATCH" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Create_RejectsMalformedDefectTypes(string body, string reasonFragment)
    {
        var resp = await Admin().PostAsync("/api/u-vision/scenarios",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(reasonFragment, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_AcceptsTypedInspection()
    {
        var resp = await Admin().PostAsync("/api/u-vision/scenarios", JsonContent.Create(new
        {
            name = "typed",
            classes = new object[]
            {
                new { id = "OK", settleable = true },
                new { id = "scratch", must_catch = true },
                new { id = "dent", must_catch = true },
            },
        }));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        return client;
    }
}
