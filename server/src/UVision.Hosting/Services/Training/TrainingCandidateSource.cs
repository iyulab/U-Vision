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
    public async Task<TrainingCandidates> LoadAsync(string scenarioId, CancellationToken cancellationToken)
    {
        var candidates = new List<TrainingCandidate>();
        var orphans = 0;
        foreach (var date in await inspections.ListDatesAsync(scenarioId, cancellationToken))
        {
            var results = (await inspections.ListAsync(scenarioId, date, cancellationToken))
                .ToDictionary(r => r.ImageId, StringComparer.Ordinal);
            foreach (var label in await labels.ListAsync(scenarioId, date, cancellationToken))
            {
                if (label.OperativeLabel is not { } operative)
                    continue;
                if (!results.TryGetValue(label.ImageId, out var result))
                {
                    orphans++;
                    continue;
                }
                candidates.Add(new TrainingCandidate(
                    label.ImageId,
                    date,
                    operative,
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
