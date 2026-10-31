using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Storage;

namespace UVision.Api.Services.Training;

/// <summary>
/// 학습 후보를 읽는다 — 사람 라벨 사이드카와 같은 날의 판정 레코드를 image_id 로 잇는다. 레코드가 없는 라벨(짝 이미지가
/// 사라진 orphan — 수만 센다)과 사람 확정 라벨이 없는 사이드카(오라클 소견뿐)는 후보가 아니다. 읽기 전용.
/// </summary>
public sealed class TrainingCandidateSource(IInspectionStore inspections, ILabelStore labels)
{
    // 라벨마다 짝 레코드 한 건을 읽는다 — 왕복 지연이 시간을 정하므로 병렬로.
    private const int ReadParallelism = 16;

    public async Task<TrainingCandidates> LoadAsync(string scenarioId, CancellationToken cancellationToken)
    {
        var candidates = new List<TrainingCandidate>();
        var orphans = 0;
        foreach (var date in await inspections.ListDatesAsync(scenarioId, cancellationToken))
        {
            // 라벨 쪽에서 출발한다: 날짜의 판정 레코드 전부를 여는 대신(건마다 한 파일 — 라벨보다 훨씬 많다) 확정 라벨의 짝 레코드만 읽는다.
            // 순서는 이미지 id 로 고정한다(라벨 색인의 순서에 기대지 않는다).
            var confirmed = (await labels.ListAsync(scenarioId, date, cancellationToken))
                .Where(l => l.OperativeLabel is not null)
                .OrderBy(l => l.ImageId, StringComparer.Ordinal)
                .ToList();
            var results = new StoredResult?[confirmed.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, confirmed.Count),
                new ParallelOptions { MaxDegreeOfParallelism = ReadParallelism, CancellationToken = cancellationToken },
                async (i, ct) => results[i] = await inspections.ReadResultAsync(scenarioId, date, confirmed[i].ImageId, ct));

            for (var i = 0; i < confirmed.Count; i++)
            {
                var label = confirmed[i];
                if (results[i] is not { } result)
                {
                    orphans++;
                    continue;
                }
                candidates.Add(new TrainingCandidate(
                    label.ImageId,
                    date,
                    label.OperativeLabel!,
                    label.History?.LastOrDefault(e => e.Mode == LabelMode.Label)?.At ?? label.Timestamp,
                    label.Audit?.Status == LabelAuditStatus.Conflicted,
                    result.RequiresReview == true || result.Agreement == false,
                    label.OperativeStream));
            }
        }
        return new TrainingCandidates(candidates, orphans);
    }
}

/// <summary>읽은 후보와, 판정 레코드가 없어 후보가 되지 못한 라벨 수.</summary>
public sealed record TrainingCandidates(IReadOnlyList<TrainingCandidate> Items, int Orphans);
