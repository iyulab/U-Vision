using Microsoft.AspNetCore.Mvc;
using UVision.Api.Models;
using UVision.Api.Services.Questions;

namespace UVision.Api.Endpoints;

/// <summary>
/// 문제 큐 엔드포인트 — 사람이 한 번에 한 사진씩 «이 사진은 무엇인가» 를 푼다. 작업자 운영 화면이 쓰므로 <b>무인증</b>이다
/// (사람 라벨과 같은 운영 데이터). 답은 학습용 표식이 붙은 라벨 또는 기권으로 기록된다.
/// </summary>
public static class QuestionEndpoints
{
    public static void MapQuestionEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/questions/next", NextAsync)
            .WithName("GetNextQuestion")
            .Produces<QuestionResponse>()
            .Produces(204)
            .ProducesProblem(400)
            .ProducesProblem(404);
        group.MapPost("/questions/answer", AnswerAsync)
            .WithName("PostQuestionAnswer")
            .Produces<QuestionAnswerResponse>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
    }

    private static async Task<IResult> NextAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        QuestionService questions,
        CancellationToken cancellationToken,
        [FromQuery(Name = "by")] string? by = null)
    {
        try
        {
            return await questions.NextAsync(scenarioId, by ?? "", cancellationToken) switch
            {
                QuestionOutcome.Asked a => Results.Ok(new QuestionResponse
                {
                    QuestionId = a.Question.QuestionId,
                    ScenarioId = a.Question.ScenarioId,
                    Date = a.Question.Date,
                    ImageId = a.Question.ImageId,
                    Form = a.Question.Form,
                    Choices = a.Choices,
                    Reason = a.Question.Reason,
                    CreatedAt = a.Question.CreatedAt,
                }),
                QuestionOutcome.NothingToAsk => Results.NoContent(),
                QuestionOutcome.ScenarioMissing => Results.Problem(statusCode: 404, detail: $"시나리오 없음: {scenarioId}"),
                QuestionOutcome.Unsupported u => Results.Problem(statusCode: 400, detail: u.Reason),
                var other => throw new InvalidOperationException($"처리하지 않은 결과: {other.GetType().Name}"),
            };
        }
        catch (ArgumentException) // 형식 위반 scenario_id → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id");
        }
    }

    private static async Task<IResult> AnswerAsync(
        QuestionAnswerInput input, QuestionService questions, CancellationToken cancellationToken)
    {
        try
        {
            return await questions.AnswerAsync(
                input.ScenarioId, input.QuestionId, input.Choice, input.Abstain, input.By ?? "", cancellationToken) switch
            {
                AnswerOutcome.Recorded r => Results.Ok(new QuestionAnswerResponse { QuestionId = r.QuestionId, Recorded = r.Kind }),
                AnswerOutcome.ScenarioMissing => Results.Problem(statusCode: 404, detail: $"시나리오 없음: {input.ScenarioId}"),
                AnswerOutcome.QuestionMissing => Results.Problem(statusCode: 404, detail: $"문제 없음: {input.QuestionId}"),
                AnswerOutcome.AlreadyClosed => Results.Problem(statusCode: 409, detail: "이미 답했거나 거둬들인 문제"),
                AnswerOutcome.AlreadyLabeled => Results.Problem(statusCode: 409, detail: "그 사이 사진에 라벨이 붙어 문제를 거둬들였다"),
                AnswerOutcome.InvalidChoice c => Results.Problem(statusCode: 400, detail: c.Detail),
                var other => throw new InvalidOperationException($"처리하지 않은 결과: {other.GetType().Name}"),
            };
        }
        catch (ArgumentException) // 형식 위반 scenario_id / question_id → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 question_id");
        }
    }
}
