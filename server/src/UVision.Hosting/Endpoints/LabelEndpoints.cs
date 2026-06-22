using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using UVision.Api.Models;
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
    }

    private static async Task<IResult> PutLabelAsync(
        LabelInput input, ILabelStore labelStore, CancellationToken cancellationToken)
    {
        if (!LabelSet.IsValid(input.Label))
            return Results.Problem(statusCode: 400, detail: $"허용되지 않은 라벨: '{input.Label}'");

        var stored = new StoredLabel
        {
            ImageId = input.ImageId,
            Label = input.Label,
            Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        try
        {
            await labelStore.WriteAsync(input.ScenarioId, input.Date, stored, cancellationToken);
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
}
