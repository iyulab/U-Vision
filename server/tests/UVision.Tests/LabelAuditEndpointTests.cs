using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>C1 — 블라인드 감사 표본(라벨 미포함) + 재라벨 제출(consistent/conflict) 계약.</summary>
public class LabelAuditEndpointTests
{
    private const string Base = "/api/u-vision";

    private static async Task LabelAsync(HttpClient c, string imageId, string label) =>
        await c.PutAsJsonAsync($"{Base}/results/label", new LabelInput
        { ScenarioId = "demo", Date = "2026-06-09", ImageId = imageId, Label = label, By = "dev" });

    [Fact]
    public async Task AuditSample_OmitsPriorLabel_BlindContract()
    {
        using var factory = new UVisionApiFactory();
        var c = factory.CreateClient();
        await LabelAsync(c, "img_a", "NG");

        var resp = await c.GetAsync($"{Base}/results/audit-sample?scenario_id=demo&date=2026-06-09");
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();

        // 블라인드: 응답 어디에도 라벨 값이 없어야 한다(image_id 만).
        Assert.DoesNotContain("\"label\"", raw);
        Assert.DoesNotContain("NG", raw);
    }

    [Fact]
    public async Task PostAudit_SameLabel_ReturnsConsistent_WithPriorLabel()
    {
        using var factory = new UVisionApiFactory();
        var c = factory.CreateClient();
        await LabelAsync(c, "img_a", "NG");

        var resp = await c.PostAsJsonAsync($"{Base}/results/audit", new AuditInput
        { ScenarioId = "demo", Date = "2026-06-09", ImageId = "img_a", Label = "NG", By = "dev" });

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("consistent", json.GetProperty("status").GetString());
        Assert.Equal("NG", json.GetProperty("prior_label").GetString());
    }

    [Fact]
    public async Task PostAudit_DifferentLabel_ReturnsConflicted()
    {
        using var factory = new UVisionApiFactory();
        var c = factory.CreateClient();
        await LabelAsync(c, "img_a", "OK");

        var resp = await c.PostAsJsonAsync($"{Base}/results/audit", new AuditInput
        { ScenarioId = "demo", Date = "2026-06-09", ImageId = "img_a", Label = "NG", By = "dev" });

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("conflicted", json.GetProperty("status").GetString());

        // 감사 후 라벨 목록에 audit 상태가 표면화된다.
        var labels = await c.GetFromJsonAsync<JsonElement>($"{Base}/results/labels?scenario_id=demo&date=2026-06-09");
        var entry = labels.EnumerateArray().First();
        Assert.Equal("conflicted", entry.GetProperty("audit").GetProperty("status").GetString());
        Assert.Equal("OK", entry.GetProperty("label").GetString()); // operative 불변
    }

    [Fact]
    public async Task PostAudit_InvalidLabel_Returns400()
    {
        using var factory = new UVisionApiFactory();
        var c = factory.CreateClient();
        await LabelAsync(c, "img_a", "NG");
        var resp = await c.PostAsJsonAsync($"{Base}/results/audit", new AuditInput
        { ScenarioId = "demo", Date = "2026-06-09", ImageId = "img_a", Label = "BOGUS", By = "dev" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostAudit_UnlabeledImage_Returns400()
    {
        using var factory = new UVisionApiFactory();
        var c = factory.CreateClient();
        var resp = await c.PostAsJsonAsync($"{Base}/results/audit", new AuditInput
        { ScenarioId = "demo", Date = "2026-06-09", ImageId = "img_missing", Label = "NG", By = "dev" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
