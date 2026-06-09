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

        foreach (var jsonPath in Directory.EnumerateFiles(dir, "*.json")
            .Where(p => !p.EndsWith(".label.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p))
        {
            await using var stream = File.OpenRead(jsonPath);
            var result = await JsonSerializer.DeserializeAsync<StoredResult>(
                stream, StoragePaths.Json, cancellationToken);
            if (result is not null)
                results.Add(result);
        }

        return results;
    }

    public async Task<InspectionImage?> ReadImageAsync(
        string scenarioId, string date, string imageId,
        CancellationToken cancellationToken = default)
    {
        var dir = _paths.DateDir(scenarioId, date); // 형식 위반이면 ArgumentException(→400)
        if (!Directory.Exists(dir))
            return null;

        var safeId = StoragePaths.Id(imageId); // 형식 위반이면 ArgumentException(→400)
        // stem 매칭(ext 무관) — 단, 같은 stem 의 결과 json({image_id}.json)은 이미지가 아니므로 제외.
        var file = Directory.EnumerateFiles(dir).FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f) == safeId
            && !Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase));
        if (file is null)
            return null;

        var data = await File.ReadAllBytesAsync(file, cancellationToken);
        return new InspectionImage(data, ContentTypeOf(file));
    }

    public Task<IReadOnlyList<string>> ListDatesAsync(
        string scenarioId, CancellationToken cancellationToken = default)
    {
        var dir = _paths.ScenarioDir(scenarioId); // 형식 위반이면 ArgumentException(→400)
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>([]);

        // 서브디렉토리 중 yyyy-MM-dd 로 파싱되는 것만(references 등은 자동 제외), 최신 먼저.
        var dates = Directory.EnumerateDirectories(dir)
            .Select(Path.GetFileName)
            .Where(name => name is not null
                && DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(dates!);
    }

    /// <summary>ISO-8601 timestamp 에서 UTC 날짜(yyyy-MM-dd) 도출.</summary>
    private static string DateOf(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string ContentTypeOf(string file) =>
        Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";
}
