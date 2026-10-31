using System.Globalization;
using System.Text.Json;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{DataPath}/{scenarioId}/questions/{question_id}.json</c> 문제 저장소. 문제를 닫는 일(읽고-고치고-쓰기)만 경합하므로
/// 하나의 잠금으로 직렬화한다 — 저장소는 한 프로세스가 연다(라벨 저장소와 같은 전제).
/// <para>
/// 목록은 파일을 다시 읽지 않고 시나리오별 메모리 색인에서 준다. 색인은 파일에서 다시 만들 수 있는 파생물이고(처음 부를 때
/// 한 번 읽는다), 이 저장소의 쓰기가 갱신한다. 문제 한 건이 파일 하나라 목록은 지금까지 낸 문제 전부를 여는 일인데, 네트워크
/// 공유에서는 파일 하나가 20 ms 안팎이라 문제가 쌓일수록 요청마다 느려진다. 문제 폴더가 사라지면(시나리오 삭제) 색인을 버린다.
/// </para>
/// </summary>
public sealed class FileQuestionStore : IQuestionStore
{
    // 처음 적재는 왕복 지연이 시간을 정하므로 병렬로 읽는다.
    private const int LoadParallelism = 16;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StoragePaths _paths;
    private readonly Dictionary<string, Dictionary<string, StoredQuestion>> _index = new(StringComparer.Ordinal);

    public FileQuestionStore(StoragePaths paths) => _paths = paths;

    public async Task CreateAsync(StoredQuestion question, CancellationToken cancellationToken = default)
    {
        var path = _paths.QuestionJson(question.ScenarioId, question.QuestionId); // 형식 위반 → ArgumentException
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
                throw new InvalidOperationException($"이미 있는 문제 id: {question.QuestionId}");
            await StoragePaths.AtomicWriteJsonAsync(path, question, cancellationToken);
            if (_index.TryGetValue(question.ScenarioId, out var loaded))
                loaded[question.QuestionId] = question;
        }
        finally { _gate.Release(); }
    }

    public async Task<StoredQuestion?> ReadAsync(
        string scenarioId, string questionId, CancellationToken cancellationToken = default) =>
        await ReadFileAsync(_paths.QuestionJson(scenarioId, questionId), cancellationToken);

    public async Task<bool> TryCloseAsync(
        string scenarioId, string questionId, string state, string? answer, string by,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.QuestionJson(scenarioId, questionId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadFileAsync(path, cancellationToken);
            if (existing is null || existing.State != QuestionState.Open)
                return false;
            var closed = existing with
            {
                State = state,
                Answer = answer,
                AnsweredBy = by,
                ClosedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            await StoragePaths.AtomicWriteJsonAsync(path, closed, cancellationToken);
            if (_index.TryGetValue(scenarioId, out var loaded))
                loaded[questionId] = closed;
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<StoredQuestion>> ListAsync(
        string scenarioId, CancellationToken cancellationToken = default)
    {
        var dir = _paths.QuestionsDir(scenarioId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(dir))
            {
                _index.Remove(scenarioId); // 시나리오가 지워졌다 — 낡은 색인을 남기지 않는다
                return [];
            }
            if (!_index.TryGetValue(scenarioId, out var loaded))
            {
                loaded = await LoadAsync(dir, cancellationToken);
                _index[scenarioId] = loaded;
            }
            return [.. loaded.Values];
        }
        finally { _gate.Release(); }
    }

    private static async Task<Dictionary<string, StoredQuestion>> LoadAsync(string dir, CancellationToken cancellationToken)
    {
        var loaded = new Dictionary<string, StoredQuestion>(StringComparer.Ordinal);
        var gate = new object();
        await Parallel.ForEachAsync(
            Directory.EnumerateFiles(dir, "*.json"),
            new ParallelOptions { MaxDegreeOfParallelism = LoadParallelism, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                if (await ReadFileAsync(path, ct) is { } q)
                    lock (gate) loaded[q.QuestionId] = q;
            });
        return loaded;
    }

    private static async Task<StoredQuestion?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredQuestion>(stream, StoragePaths.Json, cancellationToken);
    }
}
