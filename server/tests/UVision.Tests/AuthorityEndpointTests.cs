using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Storage;
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
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        var resp = await _factory.CreateClient().GetAsync(Url(sid));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("advisory", doc.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Promote_Then_Get_ReflectsStage()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });

        var promote = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote",
            new { stage = "co_primary", by = "device-x", reason = "ml strong", acknowledge_unverified = true });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        var doc = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync(Url(sid)));
        Assert.Equal("co_primary", doc.RootElement.GetProperty("stage").GetString());
    }

    [Theory]
    [InlineData("promote", "{\"stage\":\"co_primary\"}")]
    [InlineData("demote", "{}")]
    public async Task Write_MissingScenario_404_AndLeavesNoPhantomThatBlocksCreation(string action, string body)
    {
        var sid = $"ghost_{Guid.NewGuid():N}"[..12];
        var resp = await PinClient().PostAsync($"{Url(sid)}/{action}",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        // 폴더가 남았다면 같은 id 의 생성이 409 로 막힌다(읽기는 404 인데 쓰기는 «이미 있다»)
        var create = await PinClient().PostAsJsonAsync("/api/u-vision/scenarios",
            new { scenario_id = sid, name = "n", criteria = "c" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var state = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync(Url(sid)));
        Assert.Equal("advisory", state.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Promote_NonMonotonic_400()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        // advisory(기본)보다 낮은 shadow 로 promote 시도 → 400(격상은 단조 상향만)
        var resp = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "shadow" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Demote_StepsDownOne()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "ml_primary", acknowledge_unverified = true });

        var demote = await PinClient().PostAsJsonAsync($"{Url(sid)}/demote", new { by = "x" });
        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        var doc = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync(Url(sid)));
        Assert.Equal("co_primary", doc.RootElement.GetProperty("stage").GetString()); // ml_primary→co_primary
    }

    [Fact]
    public async Task Demote_AtFloor_409()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        // 기본 advisory(1) → demote → shadow(0). 다시 demote 시 floor → 409.
        await PinClient().PostAsJsonAsync($"{Url(sid)}/demote", new { by = "x" }); // advisory→shadow
        var resp = await PinClient().PostAsJsonAsync($"{Url(sid)}/demote", new { by = "x" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // --- 근거 없는 격상은 명시 확인을 요구한다(D-116) ---

    // 격상 신호를 정하는 메트릭 창을 시드한다 — 사람 라벨 불량 60 · 정상 40, 점수 있는 행. eligible=false 면 ML 이 정상을 전부 불량으로 모는 창.
    private async Task SeedWindowAsync(string sid, bool eligible)
    {
        var metrics = _factory.Services.GetRequiredService<IMetricsStore>();
        var labels = _factory.Services.GetRequiredService<ILabelStore>();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        for (var i = 0; i < 100; i++)
        {
            var ng = i < 60;
            var id = $"{(ng ? "ng" : "ok")}{i}";
            var flagged = ng || !eligible; // 불량이거나, 정상인데 ML 이 불량으로 모는 창
            await metrics.AppendAsync(sid, new MetricsRow
            {
                ImageId = id, Timestamp = now, Verdict = ng ? Verdict.NG : Verdict.OK, VlmConfidence = 0.9,
                MlLabel = flagged ? "NG" : "OK", MlConfidence = 0.9, MlNgScore = flagged ? 0.9 : 0.1,
                Agreement = true, RequiresReview = false, MlDegraded = false,
            });
            await labels.AppendLabelAsync(sid, today, id, ng ? "NG" : "OK", "test");
        }
    }

    private static async Task<JsonElement> HistoryLastAsync(HttpClient client, string sid)
    {
        var doc = JsonDocument.Parse(await client.GetStringAsync(Url(sid)));
        return doc.RootElement.GetProperty("history").EnumerateArray().Last().Clone();
    }

    [Fact]
    public async Task Promote_ToAdvisory_NeedsNoConfirmation_BecauseTheMlGainsNoAuthority()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        await PinClient().PostAsJsonAsync($"{Url(sid)}/demote", new { }); // advisory → shadow
        var resp = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "advisory" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False((await HistoryLastAsync(_factory.CreateClient(), sid)).TryGetProperty("acknowledged_unverified", out _));
    }

    [Fact]
    public async Task Promote_ToCoPrimary_WithoutEvidence_409_NamesTheReason_AndChangesNothing()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });

        var resp = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "co_primary" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var detail = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString();
        Assert.Contains("표본", detail);                       // 신호가 없다(불충분) — «적격 아님» 이 아니다
        Assert.Contains("acknowledge_unverified", detail);   // 확인하는 방법

        var state = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync(Url(sid)));
        Assert.Equal("advisory", state.RootElement.GetProperty("stage").GetString());
        Assert.Equal(0, state.RootElement.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task Promote_WithAcknowledgement_Succeeds_AndTheHistoryRecordsThatItWasUnverified()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });

        var resp = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote",
            new { stage = "co_primary", by = "console", acknowledge_unverified = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var last = await HistoryLastAsync(_factory.CreateClient(), sid);
        Assert.True(last.GetProperty("acknowledged_unverified").GetBoolean());
        Assert.Equal(["signal_insufficient"], last.GetProperty("unverified_basis").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task Promote_WithAnEligibleSignal_NeedsNoConfirmation_ButSkippingAStageStillDoes()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        await SeedWindowAsync(sid, eligible: true);
        var summary = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync(
            $"/api/u-vision/metrics?scenario_id={sid}&days=30")).RootElement;
        Assert.True(summary.GetProperty("promotion_eligible").GetBoolean()); // 시드가 «적격» 창이라는 전제

        // advisory → ml_primary 는 신호가 적격이어도 한 단계를 건너뛴다 — 확인 없이는 409, 이유는 건너뛰기만.
        var skip = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "ml_primary" });
        Assert.Equal(HttpStatusCode.Conflict, skip.StatusCode);
        var detail = JsonDocument.Parse(await skip.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString();
        Assert.Contains("건너뛰", detail);
        Assert.DoesNotContain("신호", detail);

        // 한 단계씩은 확인 없이 된다.
        var step = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "co_primary" });
        Assert.Equal(HttpStatusCode.OK, step.StatusCode);
        Assert.False((await HistoryLastAsync(_factory.CreateClient(), sid)).TryGetProperty("acknowledged_unverified", out _));
    }

    [Fact]
    public async Task Promote_WhenTheSignalIsUnmet_409_SaysUnmet_AndAConfirmedPromotionRecordsBothReasons()
    {
        var sid = $"auth_{Guid.NewGuid():N}"[..12];
        _factory.SeedScenario(new Scenario { ScenarioId = sid, Name = "a", Criteria = "c" });
        await SeedWindowAsync(sid, eligible: false);

        var refused = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "ml_primary" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var detail = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString();
        Assert.Contains("적격 아님", detail);
        Assert.Contains("건너뛰", detail);

        var ok = await PinClient().PostAsJsonAsync($"{Url(sid)}/promote", new { stage = "ml_primary", acknowledge_unverified = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var last = await HistoryLastAsync(_factory.CreateClient(), sid);
        Assert.Equal(["skipped_stage", "signal_unmet"], last.GetProperty("unverified_basis").EnumerateArray().Select(e => e.GetString()).ToArray());
    }
}
