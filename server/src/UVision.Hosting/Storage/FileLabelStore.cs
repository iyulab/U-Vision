using System.Text.Json;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{DataPath}/{scenarioId}/{date}/{image_id}.label.json</c> 사이드카 저장소.
/// 결과 json 옆에 가변 라벨을 둔다 — 결과 레코드는 건드리지 않는다(불변).
/// </summary>
public sealed class FileLabelStore : ILabelStore
{
    private readonly StoragePaths _paths;

    public FileLabelStore(StoragePaths paths) => _paths = paths;

    public Task WriteAsync(
        string scenarioId, string date, StoredLabel label,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, label.ImageId); // 형식 위반 → ArgumentException(→400)
        return StoragePaths.AtomicWriteJsonAsync(path, label, cancellationToken);
    }

    public async Task<StoredLabel?> ReadAsync(
        string scenarioId, string date, string imageId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, imageId); // 형식 위반 → ArgumentException(→400)
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredLabel>(
            stream, StoragePaths.Json, cancellationToken);
    }

    public Task DeleteAsync(
        string scenarioId, string date, string imageId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, imageId); // 형식 위반 → ArgumentException(→400)
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<StoredLabel>> ListAsync(
        string scenarioId, string date, CancellationToken cancellationToken = default)
    {
        var dir = _paths.DateDir(scenarioId, date); // 형식 위반 → ArgumentException(→400)
        var labels = new List<StoredLabel>();
        if (!Directory.Exists(dir))
            return labels;

        foreach (var path in Directory.EnumerateFiles(dir, "*.label.json"))
        {
            await using var stream = File.OpenRead(path);
            var label = await JsonSerializer.DeserializeAsync<StoredLabel>(
                stream, StoragePaths.Json, cancellationToken);
            if (label is not null)
                labels.Add(label);
        }
        return labels;
    }
}
