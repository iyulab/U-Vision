using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UVision.Edge.Agent;

/// <summary>outbox 항목의 종류 — 규칙 사건(<c>/edge/v1/events</c>)과 판정이 필요한 크롭(<c>/v2/inspect</c>).</summary>
public static class OutboxKinds
{
    public const string Event = "event";
    public const string Crop = "crop";
}

/// <summary>
/// outbox 항목 하나(= 디스크의 파일 하나). <c>order</c> 는 큐 순서(모든 항목), <c>seq</c> 는 <b>사건에만</b> 붙는 노드 단조 번호다 —
/// 크롭이 번호를 먹으면 엔진이 없는 구멍을 보고하게 된다.
/// </summary>
public sealed record OutboxItem
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("order")] public required long Order { get; init; }
    [JsonPropertyName("seq")] public long? Seq { get; init; }
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }
    /// <summary>규칙의 종류(<c>count_changed</c> …) — 사건 항목만.</summary>
    [JsonPropertyName("event_kind")] public string? EventKind { get; init; }
    [JsonPropertyName("device_time")] public required string DeviceTime { get; init; }
    [JsonPropertyName("data")] public JsonElement? Data { get; init; }
    /// <summary>크롭 파일 이름(<c>crops/</c> 안) — 크롭 항목만.</summary>
    [JsonPropertyName("crop_file")] public string? CropFile { get; init; }
    [JsonPropertyName("content_type")] public string? ContentType { get; init; }
}

/// <summary>outbox 의 지금 모습 — 서버에 «이 노드가 얼마나 밀려 있는가» 로 보고한다.</summary>
/// <param name="Depth">보내지 못한 항목 수.</param>
/// <param name="DroppedCrops">디스크 상한 때문에 버린 크롭의 누적 수.</param>
/// <param name="Bytes">outbox 가 쓰는 바이트.</param>
/// <param name="Parked">영구 거절돼 따로 둔 항목 수.</param>
public sealed record OutboxStats(int Depth, long DroppedCrops, long Bytes, int Parked);

/// <summary>
/// 에이전트의 디스크 outbox(원장 D-102 E-6) — 사건 하나 = 파일 하나(같은 디렉터리의 임시 파일 → 이름 바꾸기로 atomic), 크롭은 별도 파일.
/// <list type="bullet">
/// <item><b>재시작에 남는다</b>: 열 때 디스크를 훑어 큐 순서 · 번호를 되살린다. 쓰는 도중 멈춰 남은 임시 파일은 보내지 않고 치운다.</item>
/// <item><b>순서</b>: 큐 순서 번호가 파일 이름이라 오래된 것부터 나간다. 번호별 하위 디렉터리(1000개씩)로 열거 비용을 묶는다.</item>
/// <item><b>번호는 줄어들지도 건너뛰지도 않는다</b>: 사건의 <c>seq</c> 는 열 때 파일들의 최댓값과 저장된 고수위 가운데 큰 쪽 다음부터 이어진다.
/// 고수위 표지는 느슨하게(64건마다) 쓰므로 비정상 종료 뒤 이미 보낸 번호가 다시 쓰일 수는 있어도 — 엔진은 그것을 구멍이 아닌 늦은 도착으로
/// 센다 — 구멍을 만들 번호 건너뛰기는 없다.</item>
/// <item><b>디스크 상한</b>: 넘으면 <b>크롭부터 오래된 순으로</b> 버리고 버린 수를 센다. 사건 본문은 버리지 않는다.</item>
/// </list>
/// 한 프로세스가 한 디렉터리를 쓴다는 전제(에이전트 하나)이고 스레드 안전하다.
/// </summary>
public sealed class AgentOutbox : IDisposable
{
    public const int ItemsPerBucket = 1000;
    private const int HighWaterEvery = 64;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _root;
    private readonly long _maxBytes;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private long _nextOrder;
    private long _nextSeq;
    private long _dropped;
    private long _bytes;
    private int _sinceHighWater;

    private AgentOutbox(string root, long maxBytes, TimeProvider clock)
    {
        _root = root;
        _maxBytes = maxBytes;
        _clock = clock;
    }

    private string ItemsDir => Path.Combine(_root, "items");
    private string CropsDir => Path.Combine(_root, "crops");
    private string ParkedDir => Path.Combine(_root, "parked");
    private string StateFile => Path.Combine(_root, "state.json");

    /// <summary>outbox 를 연다 — 디스크가 정본이다. 없으면 만든다.</summary>
    public static AgentOutbox Open(string directory, long maxBytes = 512L * 1024 * 1024, TimeProvider? clock = null)
    {
        var box = new AgentOutbox(Path.GetFullPath(directory), maxBytes, clock ?? TimeProvider.System);
        box.Recover();
        return box;
    }

    private sealed record PersistedState
    {
        [JsonPropertyName("next_seq")] public long NextSeq { get; init; }
        [JsonPropertyName("next_order")] public long NextOrder { get; init; }
        [JsonPropertyName("dropped_crops")] public long DroppedCrops { get; init; }
    }

    private void Recover()
    {
        Directory.CreateDirectory(ItemsDir);
        Directory.CreateDirectory(CropsDir);
        Directory.CreateDirectory(ParkedDir);

        long maxOrder = -1, maxSeq = -1;
        _bytes = 0;
        foreach (var file in Directory.EnumerateFiles(ItemsDir, "*", SearchOption.AllDirectories))
        {
            // 쓰는 도중 멈춘 임시 파일 — 완결되지 않았으니 보내지 않고 치운다.
            if (file.EndsWith(".tmp", StringComparison.Ordinal)) { TryDelete(file); continue; }
            var item = Read(file);
            if (item is null) { TryMove(file, Path.Combine(ParkedDir, "corrupt_" + Path.GetFileName(file))); continue; }
            maxOrder = Math.Max(maxOrder, item.Order);
            if (item.Seq is { } seq) maxSeq = Math.Max(maxSeq, seq);
            _bytes += new FileInfo(file).Length;
        }
        foreach (var file in Directory.EnumerateFiles(CropsDir))
        {
            if (file.EndsWith(".tmp", StringComparison.Ordinal)) { TryDelete(file); continue; }
            _bytes += new FileInfo(file).Length;
        }

        var state = ReadState();
        _nextOrder = Math.Max(maxOrder + 1, state.NextOrder);
        _nextSeq = Math.Max(maxSeq + 1, state.NextSeq);
        _dropped = state.DroppedCrops;
    }

    /// <summary>규칙이 낸 사건을 큐에 넣는다. 반환 시점에 디스크에 완결돼 있다(재시작에도 남는다).</summary>
    public OutboxItem EnqueueEvent(string scenarioId, string eventKind, DateTimeOffset deviceTime, JsonElement? data = null)
    {
        lock (_gate)
        {
            var item = new OutboxItem
            {
                Id = Guid.CreateVersion7(_clock.GetUtcNow()).ToString("D"), Kind = OutboxKinds.Event, Order = _nextOrder++, Seq = _nextSeq++,
                ScenarioId = scenarioId, EventKind = eventKind, DeviceTime = deviceTime.ToString("o", CultureInfo.InvariantCulture), Data = data,
            };
            WriteItem(item);
            AfterWrite();
            return item;
        }
    }

    /// <summary>판정이 필요한 크롭을 큐에 넣는다(엔진의 <c>/v2/inspect</c> 로 나간다).</summary>
    public OutboxItem EnqueueCrop(string scenarioId, ReadOnlyMemory<byte> image, string contentType, DateTimeOffset deviceTime)
    {
        lock (_gate)
        {
            var id = Guid.CreateVersion7(_clock.GetUtcNow()).ToString("D");
            var cropFile = id + (contentType == "image/png" ? ".png" : ".jpg");
            WriteAtomic(Path.Combine(CropsDir, cropFile), image.Span);
            var item = new OutboxItem
            {
                Id = id, Kind = OutboxKinds.Crop, Order = _nextOrder++, ScenarioId = scenarioId,
                DeviceTime = deviceTime.ToString("o", CultureInfo.InvariantCulture), CropFile = cropFile, ContentType = contentType,
            };
            WriteItem(item);
            AfterWrite();
            return item;
        }
    }

    /// <summary>가장 오래된 것부터 최대 <paramref name="max"/> 개. 읽기만 한다.</summary>
    public IReadOnlyList<OutboxItem> Peek(int max)
    {
        lock (_gate)
            return [.. EnumerateOrdered().Take(max).Select(f => Read(f)).OfType<OutboxItem>()];
    }

    /// <summary>보냈거나 엔진이 이미 갖고 있는(duplicate) 항목을 지운다.</summary>
    public void Ack(OutboxItem item)
    {
        lock (_gate)
        {
            DeleteItem(item);
            // 고수위 표지는 느슨하게 — 매번 쓰면 큐를 비우는 비용이 두 배가 된다(비정상 종료 뒤 번호 재사용은 안전하다, 위 설명).
            if (++_sinceHighWater >= HighWaterEvery) SaveState();
        }
    }

    /// <summary>영구 거절된 항목을 <c>parked/</c> 로 옮긴다 — 다시 보내지 않고 사람이 볼 수 있게 남긴다.</summary>
    public void Park(OutboxItem item, string reason)
    {
        lock (_gate)
        {
            var path = ItemPath(item);
            if (File.Exists(path))
            {
                _bytes -= new FileInfo(path).Length;
                File.WriteAllText(Path.Combine(ParkedDir, Path.GetFileName(path) + ".reason"), reason);
                TryMove(path, Path.Combine(ParkedDir, Path.GetFileName(path)));
            }
        }
    }

    /// <summary>크롭 파일의 바이트. 상한 때문에 버려졌으면 null.</summary>
    public byte[]? ReadCrop(OutboxItem item)
    {
        if (item.CropFile is null) return null;
        var path = Path.Combine(CropsDir, item.CropFile);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public OutboxStats Stats()
    {
        lock (_gate)
            return new OutboxStats(
                Directory.EnumerateFiles(ItemsDir, "*.json", SearchOption.AllDirectories).Count(),
                _dropped, _bytes, Directory.EnumerateFiles(ParkedDir, "*.json").Count());
    }

    public void Dispose()
    {
        lock (_gate) SaveState();
    }

    // --- 내부 ----------------------------------------------------------------------------------

    private string ItemPath(OutboxItem item) =>
        Path.Combine(ItemsDir, (item.Order / ItemsPerBucket).ToString("D12", CultureInfo.InvariantCulture),
            item.Order.ToString("D20", CultureInfo.InvariantCulture) + "_" + item.Id + ".json");

    private IEnumerable<string> EnumerateOrdered() =>
        Directory.EnumerateDirectories(ItemsDir).OrderBy(d => d, StringComparer.Ordinal)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.json").OrderBy(f => f, StringComparer.Ordinal));

    private void WriteItem(OutboxItem item)
    {
        var path = ItemPath(item);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item, Json);
        WriteAtomic(path, bytes);
    }

    /// <summary>같은 디렉터리의 임시 파일에 쓰고 이름을 바꾼다 — 중간에 멈추면 임시 파일만 남는다.</summary>
    private void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
        _bytes += bytes.Length;
    }

    private static OutboxItem? Read(string path)
    {
        try { return JsonSerializer.Deserialize<OutboxItem>(File.ReadAllBytes(path), Json); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private void DeleteItem(OutboxItem item)
    {
        var path = ItemPath(item);
        if (File.Exists(path)) { _bytes -= new FileInfo(path).Length; File.Delete(path); }
        if (item.CropFile is not null)
        {
            var crop = Path.Combine(CropsDir, item.CropFile);
            if (File.Exists(crop)) { _bytes -= new FileInfo(crop).Length; File.Delete(crop); }
        }
        // 비어 버린 번호 디렉터리는 치운다 — 열거가 빈 디렉터리를 훑지 않게.
        var dir = Path.GetDirectoryName(path)!;
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) TryDeleteDir(dir);
    }

    private void AfterWrite()
    {
        if (++_sinceHighWater >= HighWaterEvery) SaveState();
        EnforceLimit();
    }

    /// <summary>상한을 넘으면 크롭부터 오래된 순으로 버린다 — 크롭 항목 파일과 그림을 함께. 사건 본문은 버리지 않는다.</summary>
    private void EnforceLimit()
    {
        if (_bytes <= _maxBytes) return;
        foreach (var file in EnumerateOrdered())
        {
            if (_bytes <= _maxBytes) break;
            if (Read(file) is { Kind: OutboxKinds.Crop } crop)
            {
                DeleteItem(crop);
                _dropped++;
            }
        }
        SaveState();
    }

    private PersistedState ReadState()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<PersistedState>(File.ReadAllBytes(StateFile), Json) ?? new PersistedState() : new PersistedState(); }
        catch (Exception ex) when (ex is JsonException or IOException) { return new PersistedState(); }
    }

    private void SaveState()
    {
        _sinceHighWater = 0;
        var tmp = StateFile + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(
            new PersistedState { NextSeq = _nextSeq, NextOrder = _nextOrder, DroppedCrops = _dropped }, Json));
        File.Move(tmp, StateFile, overwrite: true);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }
    private static void TryDeleteDir(string path) { try { Directory.Delete(path); } catch (IOException) { } }
    private static void TryMove(string from, string to) { try { File.Move(from, to, overwrite: true); } catch (IOException) { } }
}
