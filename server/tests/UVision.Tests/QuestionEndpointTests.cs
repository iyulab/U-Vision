using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>문제 큐 엔드포인트 — 무인증 운영 흐름: 문제를 받고, 사진을 보고, 답하면 학습용 표식이 붙은 라벨이 된다.</summary>
public class QuestionEndpointTests : IClassFixture<UVisionApiFactory>
{
    private readonly UVisionApiFactory _factory;
    public QuestionEndpointTests(UVisionApiFactory factory) => _factory = factory;

    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private string NewScenario()
    {
        var id = "q" + Guid.NewGuid().ToString("N")[..10];
        _factory.SeedScenario(new Scenario { ScenarioId = id, Name = "q", Criteria = "c" });
        return id;
    }

    private static async Task<string> InspectAsync(HttpClient client, string scenarioId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent(scenarioId), "scenario_id");
        var resp = await client.PostAsync("/api/u-vision/inspect", form);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("image_id").GetString()!;
    }

    [Fact]
    public async Task Next_ThenAnswer_WritesALearnStreamLabel_AndTheNextQuestionIsAnotherPhoto()
    {
        var client = _factory.CreateClient();
        var scenario = NewScenario();
        var inspected = new[] { await InspectAsync(client, scenario), await InspectAsync(client, scenario) };

        var nextResp = await client.GetAsync($"/api/u-vision/questions/next?scenario_id={scenario}&by=tablet-1");
        Assert.Equal(HttpStatusCode.OK, nextResp.StatusCode);
        var next = await nextResp.Content.ReadFromJsonAsync<JsonElement>();
        var imageId = next.GetProperty("image_id").GetString()!;
        var date = next.GetProperty("date").GetString()!;
        Assert.Contains(imageId, inspected);
        Assert.Equal("single", next.GetProperty("form").GetString());
        Assert.Equal("cold_start", next.GetProperty("reason").GetString()); // 기억 단이 꺼진 설치 — 신호가 없어 무작위로 시작
        Assert.Equal(["OK", "NG"], next.GetProperty("choices").EnumerateArray().Select(e => e.GetString()!).ToArray());

        // 문제의 사진은 결과 이미지 엔드포인트로 받는다.
        var image = await client.GetAsync($"/api/u-vision/results/image?scenario_id={scenario}&date={date}&image_id={imageId}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);

        var answer = await client.PostAsJsonAsync("/api/u-vision/questions/answer", new QuestionAnswerInput
        {
            ScenarioId = scenario, QuestionId = next.GetProperty("question_id").GetString()!, Choice = "NG", By = "tablet-1",
        });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("label", (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetString());

        var labels = await (await client.GetAsync($"/api/u-vision/results/labels?scenario_id={scenario}&date={date}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var label = labels.EnumerateArray().Single(l => l.GetProperty("image_id").GetString() == imageId);
        Assert.Equal("NG", label.GetProperty("label").GetString());
        Assert.Equal("learn", label.GetProperty("history")[0].GetProperty("stream").GetString());

        var second = await (await client.GetAsync($"/api/u-vision/questions/next?scenario_id={scenario}&by=tablet-1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(imageId, second.GetProperty("image_id").GetString());
    }

    [Fact]
    public async Task Next_Is204_WhenThereIsNothingToAsk()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/u-vision/questions/next?scenario_id={NewScenario()}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Abstain_IsRecordedAsAnAbstention_NotALabel()
    {
        var client = _factory.CreateClient();
        var scenario = NewScenario();
        await InspectAsync(client, scenario);
        var next = await (await client.GetAsync($"/api/u-vision/questions/next?scenario_id={scenario}")).Content.ReadFromJsonAsync<JsonElement>();

        var answer = await client.PostAsJsonAsync("/api/u-vision/questions/answer", new QuestionAnswerInput
        {
            ScenarioId = scenario, QuestionId = next.GetProperty("question_id").GetString()!, Abstain = true,
        });

        Assert.Equal("abstain", (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetString());
        var date = next.GetProperty("date").GetString();
        var labels = await (await client.GetAsync($"/api/u-vision/results/labels?scenario_id={scenario}&date={date}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var entry = labels.EnumerateArray().Single();
        Assert.False(entry.TryGetProperty("label", out _)); // 라벨이 아니다
        Assert.Equal("abstain", entry.GetProperty("history")[0].GetProperty("mode").GetString());
    }

    [Fact]
    public async Task BadRequests_AreMappedToProblemStatuses()
    {
        var client = _factory.CreateClient();
        var scenario = NewScenario();
        await InspectAsync(client, scenario);
        var next = await (await client.GetAsync($"/api/u-vision/questions/next?scenario_id={scenario}")).Content.ReadFromJsonAsync<JsonElement>();
        var qid = next.GetProperty("question_id").GetString()!;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/u-vision/questions/next?scenario_id=missing-scenario")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/u-vision/questions/next?scenario_id=..%2Fetc")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/u-vision/questions/answer",
            new QuestionAnswerInput { ScenarioId = scenario, QuestionId = qid, Choice = "MAYBE" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/u-vision/questions/answer",
            new QuestionAnswerInput { ScenarioId = scenario, QuestionId = "nope", Choice = "OK" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/u-vision/questions/answer",
            new QuestionAnswerInput { ScenarioId = scenario, QuestionId = qid, Choice = "OK" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/u-vision/questions/answer",
            new QuestionAnswerInput { ScenarioId = scenario, QuestionId = qid, Choice = "NG" })).StatusCode);
    }
}
