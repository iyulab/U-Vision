using System.Globalization;
using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Services.Label;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{DataPath}/{scenarioId}/{date}/{image_id}.label.json</c> 사이드카 저장소.
/// 결과 json 옆에 가변 라벨을 둔다 — 결과 레코드는 건드리지 않는다(불변).
/// </summary>
public sealed class FileLabelStore : ILabelStore
{
    private readonly StoragePaths _paths;

    public FileLabelStore(StoragePaths paths) => _paths = paths;

    public async Task AppendLabelAsync(
        string scenarioId, string date, string imageId, string label, string by,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, imageId); // 형식 위반 → ArgumentException(→400)
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var existing = await ReadNormalizedAsync(path, cancellationToken);

        var history = new List<LabelEvent>(existing?.History ?? [])
        {
            new() { Label = label, By = by, At = now, Mode = LabelMode.Label },
        };
        // 충돌 상태에서의 라벨 쓰기 = 사람 해소 → resolved. 그 외엔 직전 audit 보존.
        var prevStatus = existing?.Audit?.Status ?? LabelAuditStatus.Unaudited;
        var audit = prevStatus == LabelAuditStatus.Conflicted
            ? new LabelAudit { Status = LabelAuditStatus.Resolved, At = now }
            : existing?.Audit ?? new LabelAudit { Status = LabelAuditStatus.Unaudited };

        var updated = new StoredLabel
        {
            ImageId = imageId, Label = label, Timestamp = now, History = history, Audit = audit,
        };
        await StoragePaths.AtomicWriteJsonAsync(path, updated, cancellationToken);
    }

    public async Task<AuditOutcome> AppendAuditAsync(
        string scenarioId, string date, string imageId, string auditLabel, string by,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, imageId); // 형식 위반 → ArgumentException(→400)
        var existing = await ReadNormalizedAsync(path, cancellationToken)
            ?? throw new InvalidOperationException("감사 대상 라벨이 없습니다.");
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var status = LabelAuditEvaluator.EvaluateAuditStatus(existing.Label, auditLabel);

        var history = new List<LabelEvent>(existing.History!)
        {
            new() { Label = auditLabel, By = by, At = now, Mode = LabelMode.Audit },
        };
        // operative(최상위 label/timestamp) 불변 — audit 는 측정이지 운영 라벨이 아니다.
        var updated = existing with
        {
            History = history,
            Audit = new LabelAudit { Status = status, At = now },
        };
        await StoragePaths.AtomicWriteJsonAsync(path, updated, cancellationToken);
        return new AuditOutcome { Status = status, PriorLabel = existing.Label };
    }

    public async Task<StoredLabel?> ReadAsync(
        string scenarioId, string date, string imageId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, imageId); // 형식 위반 → ArgumentException(→400)
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        var raw = await JsonSerializer.DeserializeAsync<StoredLabel>(
            stream, StoragePaths.Json, cancellationToken);
        return raw?.Normalized();
    }

    public Task DeleteAsync(
        string scenarioId, string date, string imageId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, imageId); // 형식 위반 → ArgumentException(→400)
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<StoredLabel>> ListAsync(
        string scenarioId, string date, CancellationToken cancellationToken = default)
    {
        var dir = _paths.DateDir(scenarioId, date); // 형식 위반 → ArgumentException(→400)
        var labels = new List<StoredLabel>();
        if (!Directory.Exists(dir))
            return labels;

        foreach (var path in Directory.EnumerateFiles(dir, "*.label.json"))
        {
            await using var stream = File.OpenRead(path);
            var label = await JsonSerializer.DeserializeAsync<StoredLabel>(
                stream, StoragePaths.Json, cancellationToken);
            if (label is not null)
                labels.Add(label.Normalized());
        }
        return labels;
    }

    private static async Task<StoredLabel?> ReadNormalizedAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        var raw = await JsonSerializer.DeserializeAsync<StoredLabel>(stream, StoragePaths.Json, ct);
        return raw?.Normalized();
    }
}
