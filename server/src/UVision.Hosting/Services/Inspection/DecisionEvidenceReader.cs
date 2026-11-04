using HoneAI;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Services.Inspection;

/// <summary>
/// 판정 근거 읽기 — 판정 레코드와 그 날의 결정 장부 줄을 한 건의 <see cref="DecisionEvidence"/> 로 합친다.
/// <b>읽기 전용</b>이다: 판정원을 다시 부르지 않고, 장부·레코드에 아무것도 쓰지 않는다.
/// 레코드가 없으면 null(404). 장부 줄이 없으면(옛 기록 · 장부 쓰기 실패) 레코드에서 나온 것만 담고
/// <see cref="DecisionEvidence.LedgerRecorded"/> 를 false 로 둔다.
/// </summary>
public sealed class DecisionEvidenceReader(
    IInspectionStore inspections, ILabelStore labels, RejudgeLog rejudgeLog, StoragePaths paths)
{
    /// <exception cref="ArgumentException">scenario_id · date · image_id 형식 위반 — 엔드포인트에서 400.</exception>
    public async Task<DecisionEvidence?> ReadAsync(
        string scenarioId, string date, string imageId, CancellationToken cancellationToken)
    {
        var record = await inspections.ReadResultAsync(scenarioId, date, imageId, cancellationToken);
        if (record is null)
            return null;

        var line = await FindLedgerLineAsync(scenarioId, date, imageId, cancellationToken);
        var sidecar = await labels.ReadAsync(scenarioId, date, imageId, cancellationToken);
        var rejudges = await rejudgeLog.ReadAsync(scenarioId, date, imageId, cancellationToken);
        var provenance = line?.Provenance;
        return new DecisionEvidence
        {
            ImageId = record.ImageId,
            ScenarioId = record.ScenarioId,
            Date = date,
            Class = record.Class ?? record.Verdict.ToString(),
            Confidence = record.Confidence,
            Findings = string.IsNullOrEmpty(record.Findings) ? null : record.Findings,
            RequiresReview = record.RequiresReview,
            Posture = record.Posture,
            Agreement = record.Agreement,
            Ml = record.Ml,
            Count = record.Count,
            HumanLabel = sidecar?.OperativeLabel,
            OracleOpinion = sidecar?.LatestOracleOpinion,
            Rejudges = rejudges.Count > 0 ? rejudges : null,
            LedgerRecorded = provenance is not null,
            SettledBy = provenance?.TierId is { } tierId
                ? new SettledBy { Tier = tierId, Kind = provenance.Kind.ToString().ToLowerInvariant() }
                : null,
            TierVersion = provenance?.TierVersion,
            Path = provenance?.Path?.Select(EvidenceStep.From).ToList(),
            Annotations = provenance?.Annotations is { Count: > 0 } annotations
                ? annotations.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
                : null,
        };
    }

    /// <summary>그 날 장부에서 이 판정의 첫 줄 — 장부가 없는 날 · 줄이 없는 판정은 null.</summary>
    private async Task<ProvenanceRecord?> FindLedgerLineAsync(
        string scenarioId, string date, string imageId, CancellationToken cancellationToken)
    {
        var ledger = paths.LedgerJsonl(scenarioId, date);
        if (!File.Exists(ledger))
            return null;
        await foreach (var line in new JsonlProvenanceSink(ledger).ReadAsync(cancellationToken: cancellationToken))
            if (string.Equals(line.SubjectId, imageId, StringComparison.Ordinal))
                return line;
        return null;
    }
}
