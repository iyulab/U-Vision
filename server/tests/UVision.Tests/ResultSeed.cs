using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Tests;

/// <summary>
/// 시험이 «이미 판정된 이미지» 를 만든다. 사람 라벨 · 감사는 판정 레코드에 매인 사실이라 레코드가 없으면 404 이므로,
/// 라벨 길을 시험하는 쪽은 라벨 전에 레코드를 심는다(진짜 <c>/inspect</c> 를 부르지 않고도).
/// </summary>
internal static class ResultSeed
{
    public static async Task SeedResultAsync(this UVisionApiFactory factory, string scenarioId, string date, string imageId)
    {
        var store = factory.Services.GetRequiredService<IInspectionStore>();
        await store.SaveAsync(new byte[] { 1, 2, 3 }, ".jpg", new StoredResult
        {
            ScenarioId = scenarioId, ImageId = imageId, Verdict = Verdict.OK,
            Findings = "", Confidence = 0.9, Timestamp = date + "T00:00:00Z", ImageFile = imageId + ".jpg",
        }, default);
    }
}
