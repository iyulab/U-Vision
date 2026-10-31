using Microsoft.AspNetCore.Mvc;
using UVision.Api.Models;
using UVision.Api.Services.Questions;
using UVision.Api.Storage;

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
        group.MapGet("/questions/image", ServeImageAsync)
            .WithName("GetQuestionImage")
            .Produces(200, typeof(Stream), "image/jpeg", "image/png")
            .ProducesProblem(400)
            .ProducesProblem(404);
        group.MapPost("/questions/answer", AnswerAsync)
            .WithName("PostQuestionAnswer")
            .Produces<QuestionAnswerResponse>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
    }

    /// <summary>
    /// 문제의 사진 — 풀이자 화면이 모든 문제에 쓰는 한 경로다. 판정 결과 사진이든 정답을 아는 기준 이미지든 같은 주소 모양이라 주소만으로
    /// 심은 문제를 가려낼 수 없고(기준 이미지 경로는 주소에 정답이 든다), 정답은 응답에도 실리지 않는다.
    /// </summary>
    private static async Task<IResult> ServeImageAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        [FromQuery(Name = "question_id")] string questionId,
        IQuestionStore questions,
        IInspectionStore inspections,
        IReferenceStore references,
        CancellationToken cancellationToken)
    {
        try
        {
            var question = await questions.ReadAsync(scenarioId, questionId, cancellationToken);
            if (question is null)
                return Results.Problem(statusCode: 404, detail: $"문제 없음: {questionId}");

            if (question.Source == QuestionSource.Reference && question.SourceRef is not null)
            {
                // 정답이 OK 인 것은 OK 기준 이미지뿐이다(심을 때 그렇게 정했다) — 나머지는 NG 기준 이미지.
                var label = question.SourceTruth == ScenarioVocabulary.Ok ? ReferenceLabel.Ok : ReferenceLabel.Ng;
                var reference = await references.ReadAsync(scenarioId, label, question.SourceRef, cancellationToken);
                return reference is null
                    ? Results.Problem(statusCode: 404, detail: "문제 사진 없음")
                    : Results.File(reference.Data, reference.ContentType);
            }

            var image = await inspections.ReadImageAsync(scenarioId, question.Date, question.ImageId, cancellationToken);
            return image is null
                ? Results.Problem(statusCode: 404, detail: "문제 사진 없음")
                : Results.File(image.Data, image.ContentType);
        }
        catch (ArgumentException) // 형식 위반 scenario_id / question_id → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id/question_id");
        }
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
                    // 심은 문제는 날짜 · 사진 id 도 일반 문제의 모양으로 가린다(사진은 어느 쪽이든 question_id 로 받는다).
                    Date = a.Question.Source is null ? a.Question.Date : a.Question.CreatedAt[..10],
                    ImageId = a.Question.Source is null ? a.Question.ImageId : a.Question.QuestionId,
                    Form = a.Question.Form,
                    Choices = a.Choices,
                    // 심은 문제의 이유는 풀이자에게 가린다 — 정답률이 풀이자 신뢰로 읽히려면 일반 문제와 겉으로 같아야 한다.
                    Reason = a.Question.Source is null ? a.Question.Reason : QuestionReason.Random,
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
