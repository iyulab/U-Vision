using System.Runtime.CompilerServices;
using HoneAI;
using UVision.Api.Storage;

namespace UVision.Api.Services.Memory;

/// <summary>재생 · 곡선이 읽는 판정 한 건 — 장부 annotation 과 그 판정에 붙은 사람 라벨(없으면 null).</summary>
public sealed record LedgerEntry(IReadOnlyDictionary<string, string>? Annotations, string? HumanLabel);

/// <summary>
/// 결정 장부를 날짜 순으로 읽어 사람 라벨과 잇는다 — 문턱 재생(<see cref="MemoryReplay"/>)과 플라이휠 곡선(<see cref="FlywheelCurve"/>)이
/// 같은 읽기를 쓴다. 라벨은 판정이 든 날의 기록에 붙는다. 장부가 없는 날은 건너뛴다. 읽기 전용이다.
/// </summary>
public static class ReplaySource
{
    /// <param name="days">읽을 날짜(<c>yyyy-MM-dd</c>) — 주어진 순서대로 읽는다(시간순이려면 오래된 날부터).</param>
    public static async IAsyncEnumerable<LedgerEntry> ReadAsync(
        string scenarioId, IEnumerable<string> days, ILabelStore labelStore, StoragePaths paths,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var day in days)
        {
            var labels = (await labelStore.ListAsync(scenarioId, day, cancellationToken))
                .Where(l => l.OperativeLabel is not null)
                .ToDictionary(l => l.ImageId, l => l.OperativeLabel!, StringComparer.Ordinal);
            var ledger = paths.LedgerJsonl(scenarioId, day);
            if (!File.Exists(ledger))
                continue;
            await foreach (var record in new JsonlProvenanceSink(ledger).ReadAsync(cancellationToken: cancellationToken))
            {
                labels.TryGetValue(record.SubjectId, out var label);
                yield return new LedgerEntry(record.Provenance.Annotations, label);
            }
        }
    }
}
