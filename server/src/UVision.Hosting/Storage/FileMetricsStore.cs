using System.Text;
using System.Text.Json;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{scenario}/metrics/{yyyy-MM-dd}.jsonl</c> append-only 메트릭 저장소(B3).
/// 날짜는 row timestamp(UTC)에서 도출 — 검사 결과(<see cref="FileInspectionStore"/>)와 같은 날짜 버킷.
/// <para>
/// 동시 inspect(OP-B 무손실 연속 캐던스)가 같은 날짜 파일에 append 할 수 있으므로 프로세스 내
/// 쓰기를 <see cref="SemaphoreSlim"/> 로 직렬화한다 — 단일 호스트 전제(헌법 ⑤)라 cross-process 락은
/// 불필요. 한 줄 = 한 row(jsonl), 줄 단위 atomic 성을 락이 보장한다.
/// </para>
/// </summary>
public sealed class FileMetricsStore : IMetricsStore
{
    private readonly StoragePaths _paths;
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    /// <summary>jsonl 은 1줄/1row — 들여쓰기 없는 단일 라인 직렬화(파일 정책은 공유하되 indent 만 끔).</summary>
    private static readonly JsonSerializerOptions Jsonl = new(StoragePaths.Json)
    {
        WriteIndented = false,
    };

    public FileMetricsStore(StoragePaths paths) => _paths = paths;

    public async Task AppendAsync(
        string scenarioId, MetricsRow row, CancellationToken cancellationToken = default)
    {
        var date = StoragePaths.DateBucketOf(row.Timestamp);
        var path = _paths.MetricsJsonl(scenarioId, date); // 형식 위반이면 ArgumentException
        var line = JsonSerializer.Serialize(row, Jsonl) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        await _appendLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // O_APPEND 시맨틱 — 끝에만 덧붙인다(기존 줄 무손상). 락이 인터리브를 막는다.
            await using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(bytes, cancellationToken);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public async Task<IReadOnlyList<MetricsRow>> ReadAsync(
        string scenarioId, string date, CancellationToken cancellationToken = default)
    {
        var path = _paths.MetricsJsonl(scenarioId, date); // 형식 위반이면 ArgumentException
        if (!File.Exists(path))
            return [];

        var rows = new List<MetricsRow>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            // 손상된 줄(append 도중 크래시 등)은 관측을 멈출 이유가 아니다 — skip.
            MetricsRow? row;
            try { row = JsonSerializer.Deserialize<MetricsRow>(line, Jsonl); }
            catch (JsonException) { continue; }
            if (row is not null)
                rows.Add(row);
        }
        return rows;
    }
}
