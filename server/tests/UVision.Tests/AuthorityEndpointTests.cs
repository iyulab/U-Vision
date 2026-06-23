using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UVision.Tests;

public sealed class AuthorityEndpointTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;
    public AuthorityEndpointTests(PinFactory factory) => _factory = factory;

    private HttpClient PinClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        return c;
    }

    private static string Url(string sid) => $"/api/u-vision/scenarios/{sid}/authority";

    [Fact]
    public async Task Promote_RequiresPin()
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync($"{Url("demo")}/promote", new { stage = "co_primary" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Get_DefaultsToAdvisory_NoAuth()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new UVision.Api.Models.Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        var resp = await _factory.CreateClient().GetAsync(Url(sid));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("advisory", doc.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Promote_Then_Get_ReflectsStage()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new UVision.Api.Models.Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });

        var promote = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote",
            new { stage = "co_primary", by = "device-x", reason = "ml strong" });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        var doc = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync(Url(sid)));
        Assert.Equal("co_primary", doc.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Promote_NonMonotonic_400()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new UVision.Api.Models.Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        // advisory(기본)보다 낮은 shadow 로 promote 시도 → 400(격상은 단조 상향만)
        var resp = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "shadow" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Demote_StepsDownOne()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new UVision.Api.Models.Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "ml_primary" });

        var demote = await PinClient().PostAsJsonAsync($"{Url(sid)}/demote", new { by = "x" });
        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        var doc = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync(Url(sid)));
        Assert.Equal("co_primary", doc.RootElement.GetProperty("stage").GetString()); // ml_primary→co_primary
    }

    [Fact]
    public async Task Demote_AtFloor_409()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new UVision.Api.Models.Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        // 기본 advisory(1) → demote → shadow(0). 다시 demote 시 floor → 409.
        await PinClient().PostAsJsonAsync($"{Url(sid)}/demote", new { by = "x" }); // advisory→shadow
        var resp = await PinClient().PostAsJsonAsync($"{Url(sid)}/demote", new { by = "x" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }
}
