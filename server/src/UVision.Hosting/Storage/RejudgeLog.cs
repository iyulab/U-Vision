using System.Text;
using System.Text.Json;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 재판정 기록 — <c>{scenario}/rejudge/{yyyy-MM-dd}.jsonl</c> 에 요청 한 건 = 한 줄(<see cref="RejudgeResponse"/>)을 덧붙인다.
/// 결정 장부 · 메트릭과 <b>섞지 않는다</b>: 사용자가 물어본 소견은 판정이 아니고 곡선 · 관문의 입력도 아니다.
/// 같은 호스트의 요청들이 한 파일에 쓰므로 프로세스 안 쓰기를 직렬화한다(단일 호스트 전제).
/// </summary>
public sealed class RejudgeLog(StoragePaths paths)
{
    private static readonly JsonSerializerOptions Jsonl = new(StoragePaths.Json) { WriteIndented = false };
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    /// <exception cref="ArgumentException">scenario_id · date 형식 위반.</exception>
    public async Task AppendAsync(RejudgeResponse response, CancellationToken cancellationToken = default)
    {
        var path = paths.RejudgeJsonl(response.ScenarioId, response.Date);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, Jsonl) + "\n");
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

    /// <summary>그 판정에 대한 재판정을 요청 시각 순으로. 없으면 빈 목록. 잘린 줄은 건너뛴다.</summary>
    public async Task<IReadOnlyList<RejudgeResponse>> ReadAsync(
        string scenarioId, string date, string imageId, CancellationToken cancellationToken = default)
    {
        var path = paths.RejudgeJsonl(scenarioId, date);
        if (!File.Exists(path))
            return [];
        var found = new List<RejudgeResponse>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            RejudgeResponse? row;
            try { row = JsonSerializer.Deserialize<RejudgeResponse>(line, Jsonl); }
            catch (JsonException) { continue; }
            if (row is not null && string.Equals(row.ImageId, imageId, StringComparison.Ordinal))
                found.Add(row);
        }
        return found;
    }
}
