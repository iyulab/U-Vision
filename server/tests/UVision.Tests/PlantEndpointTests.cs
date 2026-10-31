using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using UVision.Api;
using UVision.Api.Models;
using Xunit;

namespace UVision.Tests;

/// <summary>심기를 켠(<c>PlantRate</c> 1) 팩토리 — 심은 문제가 풀이자에게 어떻게 보이는지 본다.</summary>
public sealed class PlantRateFactory : UVisionApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting($"{UVisionOptions.SectionName}:Questions:PlantRate", "1");
    }
}

public class PlantEndpointTests : IClassFixture<PlantRateFactory>
{
    private readonly PlantRateFactory _factory;
    public PlantEndpointTests(PlantRateFactory factory) => _factory = factory;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    [Fact]
    public async Task APlantedQuestion_HidesItsReason_AndAnAnswerIsRecordedLikeAnyOther()
    {
        var scenario = "p" + Guid.NewGuid().ToString("N")[..10];
        _factory.SeedScenario(new Scenario { ScenarioId = scenario, Name = "p", Criteria = "c" });
        _factory.SeedReference(scenario, ReferenceLabel.Ok, Jpeg);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/api/u-vision/questions/next?scenario_id={scenario}&by=tablet-1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var next = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // 풀이자에게 보이는 이유는 일반 문제의 값 — 정답을 아는 문제라는 것이 응답에서 드러나지 않는다.
        Assert.Equal("random", next.GetProperty("reason").GetString());
        Assert.DoesNotContain("plant", next.ToString());
        Assert.DoesNotContain("source", next.ToString());
        Assert.DoesNotContain("reference", next.ToString()); // 날짜 · 사진 id 에도 기준 이미지임이 새지 않는다
        Assert.DoesNotContain("ref_", next.ToString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", next.GetProperty("date").GetString()!);

        // 사진은 문제의 중립 경로로 받는다 — 주소에 정답(ok / ng)이 들지 않고 바이트는 그 기준 이미지다.
        var questionId = next.GetProperty("question_id").GetString()!;
        var image = await client.GetAsync($"/api/u-vision/questions/image?scenario_id={scenario}&question_id={questionId}");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(Jpeg, await image.Content.ReadAsByteArrayAsync());

        var answer = await client.PostAsJsonAsync("/api/u-vision/questions/answer", new QuestionAnswerInput
        {
            ScenarioId = scenario, QuestionId = next.GetProperty("question_id").GetString()!, Choice = "OK", By = "tablet-1",
        });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("label", (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetString());
    }

    [Fact]
    public async Task TheQuestionImageRoute_AnswersAnUnknownQuestionWith404_AndAMalformedIdWith400()
    {
        var scenario = "p" + Guid.NewGuid().ToString("N")[..10];
        _factory.SeedScenario(new Scenario { ScenarioId = scenario, Name = "p", Criteria = "c" });
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/u-vision/questions/image?scenario_id={scenario}&question_id=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/u-vision/questions/image?scenario_id={scenario}&question_id=..%2Fx")).StatusCode);
    }
}
