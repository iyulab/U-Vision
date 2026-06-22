using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Storage;

namespace UVision.Api.Endpoints;

/// <summary>
/// 사람 라벨 엔드포인트 — 검사 결과에 사후 OK/NOK 를 붙인다(선택적 ground-truth 수집).
/// <para>
/// <b>무인증</b>: 라벨은 시나리오·기준이미지 같은 관리 설정이 아니라 inspect 와 같은 운영 데이터다.
/// 라벨 쓰기/삭제는 불변 VLM 레코드(<c>{image_id}.json</c>)를 건드리지 않는다 — 사이드카만 쓴다.
/// </para>
/// </summary>
public static class LabelEndpoints
{
    public static void MapLabelEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/results/label", PutLabelAsync);
        group.MapDelete("/results/label", DeleteLabelAsync);
        group.MapGet("/results/labels", ListLabelsAsync);
        group.MapGet("/results/audit-sample", AuditSampleAsync);
        group.MapPost("/results/audit", PostAuditAsync);
    }

    private static async Task<IResult> PutLabelAsync(
        LabelInput input, ILabelStore labelStore, CancellationToken cancellationToken)
    {
        if (!LabelSet.IsValid(input.Label))
            return Results.Problem(statusCode: 400, detail: $"허용되지 않은 라벨: '{input.Label}'");

        try
        {
            await labelStore.AppendLabelAsync(
                input.ScenarioId, input.Date, input.ImageId, input.Label, input.By ?? "", cancellationToken);
        }
        catch (ArgumentException) // 형식 위반 scenario_id/date/image_id → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id/date/image_id");
        }
        return Results.Ok();
    }

    private static async Task<IResult> DeleteLabelAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        [FromQuery(Name = "date")] string date,
        [FromQuery(Name = "image_id")] string imageId,
        ILabelStore labelStore,
        CancellationToken cancellationToken)
    {
        try
        {
            await labelStore.DeleteAsync(scenarioId, date, imageId, cancellationToken);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id/date/image_id");
        }
        return Results.Ok();
    }

    private static async Task<IResult> ListLabelsAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        [FromQuery(Name = "date")] string date,
        ILabelStore labelStore,
        CancellationToken cancellationToken)
    {
        try
        {
            var labels = await labelStore.ListAsync(scenarioId, date, cancellationToken);
            return Results.Ok(labels);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 date");
        }
    }

    private static async Task<IResult> AuditSampleAsync(
        [FromQuery(Name = "scenario_id")] string scenarioId,
        [FromQuery(Name = "date")] string date,
        ILabelStore labelStore,
        IOptions<LabelAuditOptions> options,
        CancellationToken cancellationToken)
    {
        try
        {
            var labels = await labelStore.ListAsync(scenarioId, date, cancellationToken);
            // 미감사(unaudited)만 표본 후보 — 결정론 해시 샘플링. 블라인드: image_id 만 반환.
            var sample = labels
                .Where(l => (l.Audit?.Status ?? LabelAuditStatus.Unaudited) == LabelAuditStatus.Unaudited)
                .Where(l => LabelAuditEvaluator.IsSampled(l.ImageId, options.Value.SampleRatePercent))
                .Select(l => l.ImageId)
                .ToArray();
            return Results.Ok(sample);
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id 또는 date");
        }
    }

    private static async Task<IResult> PostAuditAsync(
        AuditInput input, ILabelStore labelStore, CancellationToken cancellationToken)
    {
        if (!LabelSet.IsValid(input.Label))
            return Results.Problem(statusCode: 400, detail: $"허용되지 않은 라벨: '{input.Label}'");
        try
        {
            var outcome = await labelStore.AppendAuditAsync(
                input.ScenarioId, input.Date, input.ImageId, input.Label, input.By ?? "", cancellationToken);
            return Results.Ok(new { status = outcome.Status, prior_label = outcome.PriorLabel });
        }
        catch (ArgumentException) // 형식 위반 → 400
        {
            return Results.Problem(statusCode: 400, detail: "유효하지 않은 scenario_id/date/image_id");
        }
        catch (InvalidOperationException) // 미라벨 이미지 감사 시도 → 400
        {
            return Results.Problem(statusCode: 400, detail: "감사 대상 라벨이 없습니다.");
        }
    }
}
