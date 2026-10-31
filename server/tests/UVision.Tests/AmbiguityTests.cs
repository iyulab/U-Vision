using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Questions;
using Xunit;

namespace UVision.Tests;

/// <summary>«모르겠음» 비율 — 순수 계산과 읽기 엔드포인트.</summary>
public class AmbiguitySummaryTests
{
    private static StoredQuestion Q(string state, string reason, bool planted = false) => new()
    {
        QuestionId = Guid.NewGuid().ToString("N"), ScenarioId = "s", Date = "2026-10-31", ImageId = Guid.NewGuid().ToString("N"),
        Form = QuestionForm.Single, Stream = planted ? LabelStream.Measure : LabelStream.Learn, Reason = reason,
        CreatedAt = "2026-10-31T10:00:00.0000000Z", AskedTo = "d", State = state,
        Source = planted ? QuestionSource.Reference : null,
    };

    private static List<StoredQuestion> Many(int answered, int abstained, string reason) =>
        [.. Enumerable.Range(0, answered).Select(_ => Q(QuestionState.Answered, reason)),
         .. Enumerable.Range(0, abstained).Select(_ => Q(QuestionState.Abstained, reason))];

    [Fact]
    public void RepresentativeAndTargetedReasons_AreCountedSeparately()
    {
        var qs = Many(8, 2, QuestionReason.RandomFloor)
            .Concat(Many(1, 1, QuestionReason.Random))
            .Concat(Many(0, 1, QuestionReason.ColdStart))
            .Concat(Many(3, 9, QuestionReason.Uncertain))
            .Append(Q(QuestionState.Open, QuestionReason.Uncertain));

        var s = AmbiguitySummary.Summarize("s", qs);

        Assert.Equal((9, 4, 0), (s.Representative.Answered, s.Representative.Abstained, s.Representative.Open));
        Assert.Equal((3, 9, 1), (s.Targeted.Answered, s.Targeted.Abstained, s.Targeted.Open));
        Assert.Equal(4.0 / 13, s.Representative.AbstainRate);
        Assert.Equal(9.0 / 12, s.Targeted.AbstainRate); // 어려운 사진을 골라 냈으니 높게 치우친다 — 대표 집단과 섞이지 않는다
    }

    [Fact]
    public void PlantedQuestions_AreNotCounted_BecauseTheyBelongToSolverTrust()
    {
        var s = AmbiguitySummary.Summarize("s", [Q(QuestionState.Abstained, QuestionReason.Plant, planted: true)]);

        Assert.Equal(0, s.Representative.Abstained + s.Targeted.Abstained);
    }

    [Fact]
    public void TheRateNeedsEnoughClosedQuestions()
    {
        Assert.Null(AmbiguitySummary.Summarize("s", Many(5, 4, QuestionReason.Random)).Representative.AbstainRate);      // 9건
        Assert.Equal(0.1, AmbiguitySummary.Summarize("s", Many(9, 1, QuestionReason.Random)).Representative.AbstainRate); // 10건
        Assert.Equal(AmbiguitySummary.MinAnswersForRate, AmbiguitySummary.Summarize("s", []).MinAnswersForRate);
    }

    [Fact]
    public void OpenQuestionsDoNotCountTowardTheRate()
    {
        var qs = Many(5, 4, QuestionReason.Random).Concat(Enumerable.Range(0, 20).Select(_ => Q(QuestionState.Open, QuestionReason.Random)));

        var s = AmbiguitySummary.Summarize("s", qs);

        Assert.Equal(20, s.Representative.Open);
        Assert.Null(s.Representative.AbstainRate);
    }
}

public class AmbiguityEndpointTests : IClassFixture<UVisionApiFactory>
{
    private readonly UVisionApiFactory _factory;
    public AmbiguityEndpointTests(UVisionApiFactory factory) => _factory = factory;

    [Fact]
    public async Task AScenarioWithoutQuestions_IsAllZero_AndAnUnknownOneIs404()
    {
        var scenario = "a" + Guid.NewGuid().ToString("N")[..10];
        _factory.SeedScenario(new Scenario { ScenarioId = scenario, Name = "a", Criteria = "c" });
        var client = _factory.CreateClient();

        var body = await (await client.GetAsync($"/api/u-vision/questions/ambiguity?scenario_id={scenario}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(AmbiguitySummary.MinAnswersForRate, body.GetProperty("min_answers_for_rate").GetInt32());
        foreach (var group in new[] { "representative", "targeted" })
        {
            Assert.Equal(0, body.GetProperty(group).GetProperty("answered").GetInt32());
            Assert.Equal(0, body.GetProperty(group).GetProperty("abstained").GetInt32());
            Assert.False(body.GetProperty(group).TryGetProperty("abstain_rate", out _));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/u-vision/questions/ambiguity?scenario_id=nope")).StatusCode);
    }
}
