using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Auth;
using UVision.Api.Models;
using UVision.Api.Services.Training;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 학습 데이터 선정 · 학습 준비 신호 — 정답은 사람 확정 라벨(열린 감사 충돌 제외), NG 쪽 · 검토 건은 전량,
/// OK 는 NG 쪽의 배수까지. 신호는 근거가 없으면 null.
/// </summary>
public class TrainingSetTests : IClassFixture<PinFactory>
{
    private readonly PinFactory _factory;

    public TrainingSetTests(PinFactory factory) => _factory = factory;

    private static readonly ScenarioTraining Rules = new() { MinPerClass = 2, OkRatioCap = 2, RebuildMinNewLabels = 2 };

    private static TrainingCandidate C(
        string id, string label, bool conflicted = false, bool hard = false, string at = "2026-10-20T00:00:00Z") =>
        new(id, "2026-10-20", label, at, conflicted, hard);

    [Fact]
    public void Select_DropsOpenAuditConflicts()
    {
        var s = TrainingSetSelector.Select([C("a", "NG"), C("b", "NG", conflicted: true)], Rules);

        Assert.Equal(["a"], s.Selected.Select(c => c.ImageId));
        Assert.Equal(1, s.DroppedConflicted);
    }

    [Fact]
    public void Select_CapsOkAtRatioOfNgSide_ButKeepsHardNegatives()
    {
        // NG 쪽 2건(유형 둘) → OK 상한 4. 검토 건 OK(h)는 상한 밖에서 늘 넣는다.
        var oks = Enumerable.Range(0, 10).Select(i => C($"ok{i}", "OK")).ToList();
        var s = TrainingSetSelector.Select([.. oks, C("s", "scratch"), C("d", "dent"), C("h", "OK", hard: true)], Rules);

        Assert.Equal(4, s.Selected.Count(c => c.Label == "OK" && !c.HardNegative));
        Assert.Contains(s.Selected, c => c.ImageId == "h");
        Assert.Equal(6, s.DroppedOkOverCap);
    }

    [Fact]
    public void Select_OkSampleIsDeterministic()
    {
        var oks = Enumerable.Range(0, 10).Select(i => C($"ok{i}", "OK")).ToList();
        var first = TrainingSetSelector.Select([.. oks, C("n", "NG")], Rules).Selected.Select(c => c.ImageId);
        var again = TrainingSetSelector.Select([C("n", "NG"), .. Enumerable.Reverse(oks)], Rules).Selected.Select(c => c.ImageId);

        Assert.Equal(first, again);
    }

    [Fact]
    public void Select_NoNgSide_KeepsAllOk()
    {
        var s = TrainingSetSelector.Select([C("a", "OK"), C("b", "OK"), C("c", "OK")], Rules);
        Assert.Equal(3, s.Selected.Count);
    }

    [Fact]
    public void Readiness_ColdStart_BuildReadyWhenEveryClassReachesTheMinimum()
    {
        var notYet = TrainingReadiness.Evaluate(["OK", "NG"], [C("a", "OK"), C("b", "OK"), C("c", "NG")], Rules, null, null);
        Assert.False(notYet.BuildReady);
        Assert.Equal([new TrainingClassCount("OK", 2), new TrainingClassCount("NG", 1)], notYet.Classes);
        Assert.Null(notYet.RebuildSuggested);

        var ready = TrainingReadiness.Evaluate(["OK", "NG"], [C("a", "OK"), C("b", "OK"), C("c", "NG"), C("d", "ng")], Rules, null, null);
        Assert.True(ready.BuildReady);
    }

    [Fact]
    public void Readiness_WithModel_CountsLabelsNewerThanItsDataset()
    {
        var selected = new[]
        {
            C("old", "NG", at: "2026-10-01T00:00:00Z"),
            C("n1", "NG", at: "2026-10-21T00:00:00Z"),
            C("n2", "OK", at: "2026-10-22T00:00:00Z"),
        };

        var r = TrainingReadiness.Evaluate(["OK", "NG"], selected, Rules, "v1", "2026-10-10T00:00:00Z");

        Assert.Null(r.BuildReady);
        Assert.Equal(2, r.NewLabelsSinceModel);
        Assert.True(r.RebuildSuggested);
    }

    [Fact]
    public void Readiness_ModelWithoutKnownDataset_IsUnknownNotFalse()
    {
        var r = TrainingReadiness.Evaluate(["OK", "NG"], [C("a", "NG")], Rules, "v1", null);

        Assert.Null(r.NewLabelsSinceModel);
        Assert.Null(r.RebuildSuggested);
    }

    [Fact]
    public async Task Endpoint_ReportsColdStartForANewScenario()
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        var create = await admin.PostAsync("/api/u-vision/scenarios", JsonContent.Create(new
        {
            name = "training",
            trust = new { training = new { min_per_class = 5, ok_ratio_cap = 2.0, rebuild_min_new_labels = 10 } },
        }));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scenario_id").GetString();

        var body = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/u-vision/datasets/readiness?scenario_id={id}");

        Assert.Equal(5, body.GetProperty("min_per_class").GetInt32());
        Assert.False(body.GetProperty("build_ready").GetBoolean());
        Assert.Equal(["OK", "NG"], body.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.False(body.TryGetProperty("rebuild_suggested", out _));
    }

    [Fact]
    public async Task Endpoint_UnknownScenario_Is404()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/u-vision/datasets/readiness?scenario_id=nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsOutOfRangeTraining()
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add(AdminPinFilter.HeaderName, PinFactory.Pin);
        var resp = await admin.PostAsync("/api/u-vision/scenarios", JsonContent.Create(new
        {
            name = "bad training",
            trust = new { training = new { min_per_class = 0, ok_ratio_cap = 2.0, rebuild_min_new_labels = 10 } },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("trust.training", await resp.Content.ReadAsStringAsync());
    }
}
