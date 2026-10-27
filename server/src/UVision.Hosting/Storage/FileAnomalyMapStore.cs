using System.Text.Json;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary><c>{scenario}/{date}/anomaly-maps/{imageId}.json</c> — 원자적 쓰기.</summary>
public sealed class FileAnomalyMapStore(StoragePaths paths) : IAnomalyMapStore
{
    public Task WriteAsync(
        string scenarioId, string date, string imageId, AnomalyMap map, CancellationToken cancellationToken = default) =>
        StoragePaths.AtomicWriteJsonAsync(paths.AnomalyMapFile(scenarioId, date, imageId), map, cancellationToken);

    public async Task<AnomalyMap?> ReadAsync(
        string scenarioId, string date, string imageId, CancellationToken cancellationToken = default)
    {
        var path = paths.AnomalyMapFile(scenarioId, date, imageId);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AnomalyMap>(await File.ReadAllBytesAsync(path, cancellationToken), StoragePaths.Json);
        }
        catch (JsonException)
        {
            return null; // 끊긴 파일 — 없는 것으로(지도는 관찰 자료다)
        }
    }
}
