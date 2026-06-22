using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UVision.Tests;

public sealed class ModelEndpointTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;
    public ModelEndpointTests(PinFactory factory) => _factory = factory;

    private HttpClient PinClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Admin-Pin", PinFactory.Pin);
        return c;
    }

    private const string Base = "/api/u-vision/scenarios/demo/models";

    [Fact]
    public async Task Register_RequiresPin()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync(Base, new { model_name = "demo-a" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Promote_RequiresPin()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsync($"{Base}/v1/promote", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Rollback_RequiresPin()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsync($"{Base}/rollback", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Register_Promote_List_Flow()
    {
        // 격리: 전용 시나리오로 버전 채번 충돌 방지.
        var scenarioId = $"flow_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new UVision.Api.Models.Scenario
        {
            ScenarioId = scenarioId,
            Name = "flow-test",
            Criteria = "test",
        });
        var client = PinClient();
        var baseUrl = $"/api/u-vision/scenarios/{scenarioId}/models";

        var reg = await client.PostAsJsonAsync(baseUrl, new { model_name = "demo-a", export_id = "exp_1" });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var regBody = JsonDocument.Parse(await reg.Content.ReadAsStringAsync());
        var version = regBody.RootElement.GetProperty("version").GetString();
        Assert.Equal("v1", version);

        var promote = await client.PostAsync($"{baseUrl}/v1/promote", null);
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        // list 는 무인증.
        var anon = _factory.CreateClient();
        var list = await anon.GetAsync(baseUrl);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal("v1", listBody.RootElement.GetProperty("active_version").GetString());
        Assert.Equal(1, listBody.RootElement.GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public async Task Promote_UnknownVersion_404()
    {
        var resp = await PinClient().PostAsync($"{Base}/v9/promote", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Rollback_NoPrevious_409()
    {
        // 격리: 전용 시나리오.
        var scenarioId = $"rbk_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new UVision.Api.Models.Scenario
        {
            ScenarioId = scenarioId,
            Name = "rollback-test",
            Criteria = "test",
        });
        var client = PinClient();
        var baseUrl = $"/api/u-vision/scenarios/{scenarioId}/models";

        await client.PostAsJsonAsync(baseUrl, new { model_name = "demo-only" });
        await client.PostAsync($"{baseUrl}/v1/promote", null); // active=v1, prev=null
        var resp = await client.PostAsync($"{baseUrl}/rollback", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Register_InvalidModelName_400()
    {
        var resp = await PinClient().PostAsJsonAsync(Base, new { model_name = "../escape" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
