using System.Globalization;
using System.Text.Json;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{DataPath}/{scenarioId}/questions/{question_id}.json</c> 문제 저장소. 문제를 닫는 일(읽고-고치고-쓰기)만 경합하므로
/// 하나의 잠금으로 직렬화한다 — 저장소는 한 프로세스가 연다(라벨 저장소와 같은 전제).
/// </summary>
public sealed class FileQuestionStore : IQuestionStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StoragePaths _paths;

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
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<StoredQuestion>> ListAsync(
        string scenarioId, CancellationToken cancellationToken = default)
    {
        var dir = _paths.QuestionsDir(scenarioId);
        var questions = new List<StoredQuestion>();
        if (!Directory.Exists(dir))
            return questions;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (await ReadFileAsync(path, cancellationToken) is { } q)
                questions.Add(q);
        }
        return questions;
    }

    private static async Task<StoredQuestion?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredQuestion>(stream, StoragePaths.Json, cancellationToken);
    }
}
