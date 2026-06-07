using System.Globalization;
using System.Text.Json;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{DataPath}/{scenarioId}/{yyyy-MM-dd}/{image_id}.(jpg|png|json)</c> 파일시스템 저장소.
/// 날짜는 결과 timestamp(UTC)에서 도출한다 — 캡처와 레코드가 같은 날짜 디렉토리에 모인다.
/// </summary>
public sealed class FileInspectionStore : IInspectionStore
{
    private readonly StoragePaths _paths;

    public FileInspectionStore(StoragePaths paths) => _paths = paths;

    public async Task SaveAsync(
        ReadOnlyMemory<byte> image,
        string ext,
        StoredResult result,
        CancellationToken cancellationToken = default)
    {
        var date = DateOf(result.Timestamp);
        var imagePath = _paths.ImageFile(result.ScenarioId, date, result.ImageId, ext);
        var resultPath = _paths.ResultFile(result.ScenarioId, date, result.ImageId);

        // 이미지 먼저, json 으로 커밋 — json 존재 = 완결 레코드.
        await StoragePaths.AtomicWriteAsync(imagePath, image, cancellationToken);
        await StoragePaths.AtomicWriteJsonAsync(resultPath, result, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredResult>> ListAsync(
        string scenarioId, string date, CancellationToken cancellationToken = default)
    {
        var dir = _paths.DateDir(scenarioId, date); // 형식 위반이면 ArgumentException(→400)
        var results = new List<StoredResult>();
        if (!Directory.Exists(dir))
            return results;

        foreach (var jsonPath in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p))
        {
            await using var stream = File.OpenRead(jsonPath);
            var result = await JsonSerializer.DeserializeAsync<StoredResult>(
                stream, StoragePaths.Json, cancellationToken);
            if (result is not null)
                results.Add(result);
        }

        return results;
    }

    /// <summary>ISO-8601 timestamp 에서 UTC 날짜(yyyy-MM-dd) 도출.</summary>
    private static string DateOf(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
