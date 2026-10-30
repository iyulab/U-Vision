using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>문제 저장소 — 문제 한 건이 파일 하나. 라벨 사이드카와 분리돼 있다.</summary>
public interface IQuestionStore
{
    /// <summary>문제를 새로 쓴다. 같은 id 가 있으면 <see cref="InvalidOperationException"/>.</summary>
    Task CreateAsync(StoredQuestion question, CancellationToken cancellationToken = default);

    /// <summary>문제를 읽는다. 없으면 null.</summary>
    Task<StoredQuestion?> ReadAsync(string scenarioId, string questionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 열린 문제를 닫는다(<paramref name="state"/> = <see cref="QuestionState.Answered"/> · <see cref="QuestionState.Abstained"/> ·
    /// <see cref="QuestionState.Withdrawn"/>). 이미 닫혔거나 없으면 false — 두 풀이자가 같은 문제에 답해도 한 번만 닫힌다.
    /// </summary>
    Task<bool> TryCloseAsync(
        string scenarioId, string questionId, string state, string? answer, string by,
        CancellationToken cancellationToken = default);

    /// <summary>시나리오의 모든 문제. 없으면 빈 목록.</summary>
    Task<IReadOnlyList<StoredQuestion>> ListAsync(string scenarioId, CancellationToken cancellationToken = default);
}
