using System.Text;
using System.Text.Json;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{scenario}/memory/{encoderKey}/vectors.jsonl</c> append-only 벡터 캐시. 쓰기는 프로세스 안에서 직렬화한다(단일 호스트
/// 전제 — <see cref="FileMetricsStore"/> 와 같다). 잘린 줄(append 도중 중단)은 건너뛴다 — 그 사례는 다음 동기화가
/// 다시 채운다.
/// </summary>
public sealed class FileMemoryVectorStore(StoragePaths paths) : IMemoryVectorStore
{
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    private static readonly JsonSerializerOptions Jsonl = new(StoragePaths.Json) { WriteIndented = false };

    public async Task AppendAsync(
        string scenarioId, string encoderKey, IReadOnlyList<MemoryVector> vectors,
        CancellationToken cancellationToken = default)
    {
        var path = paths.MemoryVectorsJsonl(scenarioId, encoderKey); // 형식 위반이면 ArgumentException
        if (vectors.Count == 0)
            return;
        var text = new StringBuilder();
        foreach (var v in vectors)
            text.Append(JsonSerializer.Serialize(new Line(StoragePaths.Id(v.ImageId), v.Vector), Jsonl)).Append('\n');
        var bytes = Encoding.UTF8.GetBytes(text.ToString());

        await _appendLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(bytes, cancellationToken);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, float[]>> ReadAsync(
        string scenarioId, string encoderKey, CancellationToken cancellationToken = default)
    {
        var path = paths.MemoryVectorsJsonl(scenarioId, encoderKey);
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return vectors;

        foreach (var text in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            Line? line;
            try { line = JsonSerializer.Deserialize<Line>(text, Jsonl); }
            catch (JsonException) { continue; }
            if (line is { ImageId: { Length: > 0 } id, Vector: { Length: > 0 } vector })
                vectors[id] = vector;
        }
        return vectors;
    }

    private sealed record Line(string ImageId, float[] Vector);
}
