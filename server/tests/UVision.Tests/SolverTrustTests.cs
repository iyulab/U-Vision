using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Questions;
using Xunit;

namespace UVision.Tests;

/// <summary>정답 심기로 잰 풀이자 정답률 — 순수 계산과 읽기 엔드포인트.</summary>
public class SolverTrustSummaryTests
{
    private static StoredQuestion Q(string state, string device, string? answer = null, string truth = "OK", bool planted = true) => new()
    {
        QuestionId = Guid.NewGuid().ToString("N"), ScenarioId = "s", Date = "2026-10-31", ImageId = "i", Form = QuestionForm.Single,
        Stream = LabelStream.Measure, Reason = QuestionReason.Plant, CreatedAt = "2026-10-31T10:00:00.0000000Z", AskedTo = device,
        State = state, Answer = answer, AnsweredBy = state == QuestionState.Open ? null : device,
        Source = planted ? QuestionSource.Reference : null, SourceRef = planted ? "r" : null, SourceTruth = planted ? truth : null,
    };

    [Fact]
    public void NoPlantedQuestions_AllZero_AndRegularQuestionsAreNotCounted()
    {
        var s = SolverTrustSummary.Summarize("s", [Q(QuestionState.Answered, "d1", "OK", planted: false)]);

        Assert.Equal(0, s.Planted);
        Assert.Empty(s.Devices);
        Assert.Null(s.Accuracy);
    }

    [Fact]
    public void CountsHitsMissesUnsureAndOpen_PerDevice()
    {
        var s = SolverTrustSummary.Summarize("s",
        [
            Q(QuestionState.Answered, "d1", "OK"),                  // 정답
            Q(QuestionState.Answered, "d1", "NG"),                  // 오답
            Q(QuestionState.Answered, "d1", "ng", truth: "NG"),     // 대소문자 무시로 정답
            Q(QuestionState.Abstained, "d1"),
            Q(QuestionState.Open, "d2"),
        ]);

        Assert.Equal(5, s.Planted);
        var d1 = s.Devices.Single(d => d.Device == "d1");
        Assert.Equal((4, 2, 1, 1, 0), (d1.Planted, d1.Correct, d1.Wrong, d1.Unsure, d1.Open));
        var d2 = s.Devices.Single(d => d.Device == "d2");
        Assert.Equal((1, 0, 0, 0, 1), (d2.Planted, d2.Correct, d2.Wrong, d2.Unsure, d2.Open));
        Assert.Equal("d1", s.Devices[0].Device); // 많이 받은 장치가 먼저
    }

    [Fact]
    public void AccuracyIsWithheld_UntilEnoughAnswers()
    {
        var nine = Enumerable.Range(0, 9).Select(_ => Q(QuestionState.Answered, "d1", "OK")).ToList();
        Assert.Null(SolverTrustSummary.Summarize("s", nine).Accuracy);          // 9건 — 점추정을 내지 않는다
        Assert.Null(SolverTrustSummary.Summarize("s", nine).Devices[0].Accuracy);

        var ten = nine.Append(Q(QuestionState.Answered, "d1", "NG")).ToList();   // 10건 중 9 정답
        var s = SolverTrustSummary.Summarize("s", ten);
        Assert.Equal(0.9, s.Accuracy!.Value, 6);
        Assert.Equal(0.9, s.Devices[0].Accuracy!.Value, 6);
        Assert.Equal(SolverTrustSummary.MinAnswersForRate, s.MinAnswersForRate);
    }

    [Fact]
    public void UnsureAndOpenDoNotCountTowardTheRate()
    {
        var qs = Enumerable.Range(0, 10).Select(_ => Q(QuestionState.Answered, "d1", "OK")).ToList();
        qs.AddRange(Enumerable.Range(0, 20).Select(_ => Q(QuestionState.Abstained, "d1")));
        qs.AddRange(Enumerable.Range(0, 5).Select(_ => Q(QuestionState.Open, "d1")));

        var d = SolverTrustSummary.Summarize("s", qs).Devices.Single();

        Assert.Equal(1.0, d.Accuracy!.Value, 6); // «모르겠음» 이 정답률을 깎지 않는다 — 어려운 입력일수록 늘 뿐
        Assert.Equal((35, 20, 5), (d.Planted, d.Unsure, d.Open));
    }
}

public class SolverTrustEndpointTests : IClassFixture<PlantRateFactory>
{
    private readonly PlantRateFactory _factory;
    public SolverTrustEndpointTests(PlantRateFactory factory) => _factory = factory;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    [Fact]
    public async Task AnsweredPlantsAppear_PerDevice_AndAnUnknownScenarioIs404()
    {
        var scenario = "t" + Guid.NewGuid().ToString("N")[..10];
        _factory.SeedScenario(new Scenario { ScenarioId = scenario, Name = "t", Criteria = "c" });
        _factory.SeedReference(scenario, ReferenceLabel.Ok, Jpeg);
        var client = _factory.CreateClient();

        var next = await (await client.GetAsync($"/api/u-vision/questions/next?scenario_id={scenario}&by=tablet-9"))
            .Content.ReadFromJsonAsync<JsonElement>();
        (await client.PostAsJsonAsync("/api/u-vision/questions/answer", new QuestionAnswerInput
        {
            ScenarioId = scenario, QuestionId = next.GetProperty("question_id").GetString()!, Choice = "OK", By = "tablet-9",
        })).EnsureSuccessStatusCode();

        var trust = await (await client.GetAsync($"/api/u-vision/questions/solver-trust?scenario_id={scenario}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, trust.GetProperty("planted").GetInt32());
        Assert.Equal(1, trust.GetProperty("correct").GetInt32());
        Assert.False(trust.TryGetProperty("accuracy", out _)); // 1건 — 정답률은 아직 없다
        var device = trust.GetProperty("devices")[0];
        Assert.Equal("tablet-9", device.GetProperty("device").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/u-vision/questions/solver-trust?scenario_id=nope")).StatusCode);
    }
}
