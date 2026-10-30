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
        var date = StoragePaths.DateBucketOf(result.Timestamp);
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

    public async Task<ResultPage> ListPageAsync(
        string scenarioId, string date, int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit 는 1 이상");
        var after = cursor is null ? ((long, string)?)null : ParseCursor(cursor);
        var dir = _paths.DateDir(scenarioId, date); // 형식 위반이면 ArgumentException(→400)
        if (!Directory.Exists(dir))
            return new ResultPage([], null);

        // 디렉터리 목록만 읽는다(이름 + 기록 시각) — 레코드 본문은 이 쪽 몫만 연다. 최신 먼저, 같은 시각이면 이름 순.
        var entries = new DirectoryInfo(dir).EnumerateFiles("*.json")
            .Where(f => !f.Name.EndsWith(".label.json", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Ticks: f.LastWriteTimeUtc.Ticks, Stem: Path.GetFileNameWithoutExtension(f.Name), Path: f.FullName))
            .OrderByDescending(e => e.Ticks)
            .ThenBy(e => e.Stem, StringComparer.Ordinal)
            .ToList();

        var start = after is { } c
            ? entries.FindIndex(e => e.Ticks < c.Item1 || (e.Ticks == c.Item1 && string.CompareOrdinal(e.Stem, c.Item2) > 0))
            : 0;
        if (start < 0)
            return new ResultPage([], null);

        var items = new List<StoredResult>(limit);
        var last = start - 1;
        for (var i = start; i < entries.Count && items.Count < limit; i++)
        {
            last = i;
            await using var stream = File.OpenRead(entries[i].Path);
            if (await JsonSerializer.DeserializeAsync<StoredResult>(stream, StoragePaths.Json, cancellationToken) is { } result)
                items.Add(result);
        }

        var more = last + 1 < entries.Count && last >= start;
        return new ResultPage(items, more ? FormatCursor(entries[last].Ticks, entries[last].Stem) : null);
    }

    private static string FormatCursor(long ticks, string stem) => ticks.ToString(CultureInfo.InvariantCulture) + "." + stem;

    // 커서는 서버가 낸 값 그대로여야 한다 — 모양이 다르면 거부(변형하지 않는다). 이름 부분은 id 화이트리스트를 통과해야 한다.
    private static (long, string) ParseCursor(string cursor)
    {
        var dot = cursor.IndexOf('.');
        if (dot < 1 || dot == cursor.Length - 1
            || !long.TryParse(cursor.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            throw new ArgumentException("읽을 수 없는 cursor", nameof(cursor));
        return (ticks, StoragePaths.Id(cursor[(dot + 1)..]));
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

    private static string ContentTypeOf(string file) =>
        Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";
}
