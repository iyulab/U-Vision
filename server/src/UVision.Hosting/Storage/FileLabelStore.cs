using System.Globalization;
using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Services.Label;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{DataPath}/{scenarioId}/{date}/{image_id}.label.json</c> 사이드카 저장소.
/// 결과 json 옆에 가변 라벨을 둔다 — 결과 레코드는 건드리지 않는다(불변).
/// <para>
/// 쓰기는 모두 읽고-고치고-쓰기다. 사람 라벨(HTTP)과 오라클 스윕(백그라운드)이 같은 사이드카를 동시에 고치면
/// 한쪽 이벤트가 사라지거나 rename 이 충돌하므로, 경로별로 직렬화한다(프로세스 안 — 저장소는 한 프로세스가 연다).
/// </para>
/// <para>
/// 날짜별 목록은 사이드카를 다시 열지 않고 메모리 색인에서 준다. 색인은 파일에서 다시 만들 수 있는 파생물이고(날짜 폴더마다
/// 처음 부를 때 한 번 병렬로 읽는다), 이 저장소의 쓰기가 갱신한다. 목록은 기억 동기화 · 지표 · 자동격하 · export 가 부르는데
/// 사이드카 한 건이 파일 하나라 네트워크 공유에서는 건당 10~20 ms 가 쌓인다. 날짜 폴더가 사라지면 색인을 버린다.
/// 엔진 밖에서 사이드카를 손으로 고치면 프로세스를 다시 띄우기 전까지 색인이 모른다(저장소는 한 프로세스가 연다는 전제와 같음).
/// </para>
/// </summary>
public sealed class FileLabelStore : ILabelStore
{
    // 경로 해시로 고른 고정 개수의 잠금 — 이미지마다 잠금을 만들지 않아 메모리가 늘지 않는다.
    private readonly SemaphoreSlim[] _stripes =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    // 처음 적재는 왕복 지연이 시간을 정하므로 병렬로 읽는다.
    private const int LoadParallelism = 16;

    private readonly StoragePaths _paths;

    // 날짜 폴더 → (이미지 id → 라벨). 적재와 색인 갱신은 한 잠금으로 직렬화한다 — 적재 중에 끝난 쓰기는 그 뒤에 색인을 고친다.
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, StoredLabel>> _index = new(StringComparer.Ordinal);

    public FileLabelStore(StoragePaths paths) => _paths = paths;

    private async Task IndexPutAsync(string scenarioId, string date, StoredLabel written, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try
        {
            if (_index.TryGetValue(_paths.DateDir(scenarioId, date), out var loaded))
                loaded[written.ImageId] = written.Normalized();
        }
        finally { _indexGate.Release(); }
    }

    private async Task<T> WithPathLockAsync<T>(string path, Func<Task<T>> update, CancellationToken ct)
    {
        var stripe = _stripes[(StringComparer.Ordinal.GetHashCode(path) & int.MaxValue) % _stripes.Length];
        await stripe.WaitAsync(ct);
        try { return await update(); }
        finally { stripe.Release(); }
    }

    private Task WithPathLockAsync(string path, Func<Task> update, CancellationToken ct) =>
        WithPathLockAsync<bool>(path, async () => { await update(); return true; }, ct);

    public async Task AppendLabelAsync(
        string scenarioId, string date, string imageId, string label, string by,
        CancellationToken cancellationToken = default, string? stream = null)
    {
        if (!LabelStream.IsValid(stream))
            throw new ArgumentException($"알 수 없는 출제 흐름 표식: '{stream}'", nameof(stream));
        var path = _paths.LabelJson(scenarioId, date, imageId); // 형식 위반 → ArgumentException(→400)
        var written = await WithPathLockAsync(path, () => AppendLabelLockedAsync(path, imageId, label, by, stream, cancellationToken), cancellationToken);
        await IndexPutAsync(scenarioId, date, written, cancellationToken);
    }

    private static async Task<StoredLabel> AppendLabelLockedAsync(
        string path, string imageId, string label, string by, string? stream, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var existing = await ReadNormalizedAsync(path, cancellationToken);

        var history = new List<LabelEvent>(existing?.History ?? [])
        {
            new() { Label = label, By = by, At = now, Mode = LabelMode.Label, Stream = stream },
        };
        // 충돌 상태에서의 라벨 쓰기 = 사람 해소 → resolved. 그 외엔 직전 audit 보존.
        var prevStatus = existing?.Audit?.Status ?? LabelAuditStatus.Unaudited;
        var audit = prevStatus == LabelAuditStatus.Conflicted
            ? new LabelAudit { Status = LabelAuditStatus.Resolved, At = now }
            : existing?.Audit ?? new LabelAudit { Status = LabelAuditStatus.Unaudited };

        // 향후 StoredLabel 에 carry-forward 필드가 추가되면 여기서 명시적으로 설정해야 한다(new 는 with 와 달리 보존 안 함).
        var updated = new StoredLabel
        {
            ImageId = imageId, Label = label, Timestamp = now, History = history, Audit = audit,
        };
        await StoragePaths.AtomicWriteJsonAsync(path, updated, cancellationToken);
        return updated;
    }

    public async Task AppendAbstainAsync(
        string scenarioId, string date, string imageId, string by,
        CancellationToken cancellationToken = default, string? stream = null)
    {
        if (!LabelStream.IsValid(stream))
            throw new ArgumentException($"알 수 없는 출제 흐름 표식: '{stream}'", nameof(stream));
        var path = _paths.LabelJson(scenarioId, date, imageId);
        var written = await WithPathLockAsync(path, () => AppendAbstainLockedAsync(path, imageId, by, stream, cancellationToken), cancellationToken);
        await IndexPutAsync(scenarioId, date, written, cancellationToken);
    }

    private static async Task<StoredLabel> AppendAbstainLockedAsync(
        string path, string imageId, string by, string? stream, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var existing = await ReadNormalizedAsync(path, cancellationToken);

        var history = new List<LabelEvent>(existing?.History ?? [])
        {
            new() { Label = "", By = by, At = now, Mode = LabelMode.Abstain, Stream = stream },
        };
        // operative(Label)·Audit 불변 — 기권은 라벨이 아니다. 미라벨이면 미라벨 그대로 남는다.
        var updated = new StoredLabel
        {
            ImageId = imageId,
            Label = existing?.OperativeLabel,
            Timestamp = existing?.Timestamp ?? now,
            History = history,
            Audit = existing?.Audit,
        };
        await StoragePaths.AtomicWriteJsonAsync(path, updated, cancellationToken);
        return updated;
    }

    public async Task AppendOracleAsync(
        string scenarioId, string date, string imageId, string label, string by,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, imageId);
        var written = await WithPathLockAsync(path, () => AppendOracleLockedAsync(path, imageId, label, by, cancellationToken), cancellationToken);
        await IndexPutAsync(scenarioId, date, written, cancellationToken);
    }

    private static async Task<StoredLabel> AppendOracleLockedAsync(
        string path, string imageId, string label, string by, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var existing = await ReadNormalizedAsync(path, cancellationToken);

        var history = new List<LabelEvent>(existing?.History ?? [])
        {
            new() { Label = label, By = by, At = now, Mode = LabelMode.Oracle },
        };
        // operative(Label)·Audit 불변 — 오라클은 비operative 2차 의견(D2 위계).
        var updated = new StoredLabel
        {
            ImageId = imageId,
            Label = existing?.OperativeLabel,                 // 미라벨이면 null
            Timestamp = existing?.Timestamp ?? now,
            History = history,
            Audit = existing?.Audit,
        };
        await StoragePaths.AtomicWriteJsonAsync(path, updated, cancellationToken);
        return updated;
    }

    public async Task<AuditOutcome> AppendAuditAsync(
        string scenarioId, string date, string imageId, string auditLabel, string by,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, imageId); // 형식 위반 → ArgumentException(→400)
        var (outcome, written) = await WithPathLockAsync(path, () => AppendAuditLockedAsync(path, auditLabel, by, cancellationToken), cancellationToken);
        await IndexPutAsync(scenarioId, date, written, cancellationToken);
        return outcome;
    }

    private static async Task<(AuditOutcome Outcome, StoredLabel Written)> AppendAuditLockedAsync(
        string path, string auditLabel, string by, CancellationToken cancellationToken)
    {
        var existing = await ReadNormalizedAsync(path, cancellationToken)
            ?? throw new InvalidOperationException("감사 대상 라벨이 없습니다.");
        var operative = existing.OperativeLabel
            ?? throw new InvalidOperationException("operative 라벨이 없어 감사할 수 없습니다(oracle-only).");
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var status = LabelAuditEvaluator.EvaluateAuditStatus(operative, auditLabel);

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
        return (new AuditOutcome { Status = status, PriorLabel = operative }, updated);
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

    public async Task DeleteAsync(
        string scenarioId, string date, string imageId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.LabelJson(scenarioId, date, imageId); // 형식 위반 → ArgumentException(→400)
        if (File.Exists(path))
            File.Delete(path);
        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            if (_index.TryGetValue(_paths.DateDir(scenarioId, date), out var loaded))
                loaded.Remove(imageId);
        }
        finally { _indexGate.Release(); }
    }

    public async Task<IReadOnlyList<StoredLabel>> ListAsync(
        string scenarioId, string date, CancellationToken cancellationToken = default)
    {
        var dir = _paths.DateDir(scenarioId, date); // 형식 위반 → ArgumentException(→400)
        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(dir))
            {
                _index.Remove(dir); // 폴더가 지워졌다 — 낡은 색인을 남기지 않는다
                return [];
            }
            if (!_index.TryGetValue(dir, out var loaded))
            {
                loaded = await LoadAsync(dir, cancellationToken);
                _index[dir] = loaded;
            }
            return [.. loaded.Values];
        }
        finally { _indexGate.Release(); }
    }

    private static async Task<Dictionary<string, StoredLabel>> LoadAsync(string dir, CancellationToken cancellationToken)
    {
        var loaded = new Dictionary<string, StoredLabel>(StringComparer.Ordinal);
        var gate = new object();
        await Parallel.ForEachAsync(
            Directory.EnumerateFiles(dir, "*.label.json"),
            new ParallelOptions { MaxDegreeOfParallelism = LoadParallelism, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                await using var stream = File.OpenRead(path);
                var label = await JsonSerializer.DeserializeAsync<StoredLabel>(stream, StoragePaths.Json, ct);
                if (label is not null)
                    lock (gate) loaded[label.ImageId] = label.Normalized();
            });
        return loaded;
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
