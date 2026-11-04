using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UVision.Api.Models;
using UVision.Api.Services.Events;
using UVision.Api.Storage;

namespace UVision.Api.Services.Edge;

/// <summary>
/// 엣지 노드의 규칙 사건 수신(원장 D-102 E-4 · E-5) — 배치를 항목별로 검증해 <c>accepted</c> / <c>duplicate</c> / <c>rejected</c> 로 답한다.
/// <list type="bullet">
/// <item><b>중복 제거</b>: 키 <c>(node_id, event_id)</c>(UUIDv7). 키마다 작은 파일 <c>seen/</c> — 같은 사건을 다시 보내면 한 건 + <c>duplicate</c>,
/// 엔진 재시작 뒤에도 같다. 오래된 표지는 지연 정리한다.</item>
/// <item><b>구멍만 감지</b>: 노드 단조 <c>seq</c> 의 수신 구간 집합을 두고 빈 구간을 알린다 — 도착 순서에 의존하지 않고, 늦게 온 사건이 구멍을 메운다.</item>
/// <item><b>시각 이중 기록</b>: <c>device_time</c> · <c>received_at</c> · <c>clock_offset_ms</c> 를 원본 그대로 쌓고 보정값은 파생으로 따로 둔다.
/// 날짜 폴더는 서버가 받은 날(UTC)이다.</item>
/// <item><b>배정된 시나리오에만</b>: 노드 레코드의 <c>scenarios</c> 밖 시나리오의 사건은 항목별로 거절한다.</item>
/// <item>받은 사건은 노드의 append-only 장부에 덧붙인 뒤(<c>seen</c> 은 그다음) 기존 사건 내보내기 outbox 에 같은 CloudEvents 봉투로 싣는다 —
/// 중간에 멈추면 중복 줄이 생길 수는 있어도 사건을 잃지 않는다(at-least-once).</item>
/// </list>
/// </summary>
public sealed class EdgeEventStore(
    StoragePaths paths, EdgeNodeStore nodes, IScenarioStore scenarios, DecisionEventPublisher publisher,
    TimeProvider clock, ILogger<EdgeEventStore> logger)
{
    public const int MaxBatch = 500;
    public const int MaxDataBytes = 16 * 1024;

    /// <summary>«받았다» 표지를 지우는 나이 — 이보다 오래된 사건이 다시 오는 일은 에이전트의 outbox 보존 밖이다.</summary>
    public static readonly TimeSpan SeenRetention = TimeSpan.FromDays(30);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private const int MaxReportedGaps = 20;

    /// <summary>오프셋 크기 상한(10년) — 그 밖은 시계가 아니라 오류다.</summary>
    private const long MaxOffsetMs = 10L * 365 * 24 * 3600 * 1000;

    private static readonly Regex Uuid7 =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z", RegexOptions.Compiled);
    private static readonly Regex KindPattern = new(@"^[a-z0-9_.-]{1,64}\z", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Compact = new(StoragePaths.Json) { WriteIndented = false };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <exception cref="ArgumentException">배치가 비었거나 500건을 넘음 · 오프셋 범위 밖 — 호출부가 400 으로.</exception>
    public async Task<EdgeEventsResponse> IngestAsync(EdgeNode node, EdgeEventsRequest request, CancellationToken ct)
    {
        if (request.Events.Count == 0 || request.Events.Count > MaxBatch)
            throw new ArgumentException($"events 는 1~{MaxBatch}건");
        if (request.ClockOffsetMs is { } offset)
        {
            if (Math.Abs(offset) > MaxOffsetMs)
                throw new ArgumentException("clock_offset_ms 가 범위 밖");
            await nodes.RecordClockOffsetAsync(node.NodeId, offset, ct);
        }

        var gate = _locks.GetOrAdd(node.NodeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var ranges = await ReadRangesAsync(node.NodeId, ct);
            var results = new List<EdgeEventResult>(request.Events.Count);
            var rangesChanged = false;
            var seenInBatch = new HashSet<string>(StringComparer.Ordinal);

            foreach (var input in request.Events)
            {
                var id = input.EventId ?? "";
                var reject = Validate(input, node, out var deviceTime);
                if (reject is null && await scenarios.GetAsync(input.ScenarioId!, ct) is null)
                    reject = "scenario_unknown";
                if (reject is not null)
                {
                    results.Add(new EdgeEventResult { EventId = id, Status = "rejected", Code = reject });
                    continue;
                }

                // 같은 배치 안의 되풀이도 중복이다.
                if (!seenInBatch.Add(id) || File.Exists(paths.EdgeSeenFile(node.NodeId, id)))
                {
                    results.Add(new EdgeEventResult { EventId = id, Status = "duplicate" });
                    continue;
                }

                var received = clock.GetUtcNow();
                var record = new EdgeEventRecord
                {
                    NodeId = node.NodeId, EventId = id, Seq = input.Seq!.Value, ScenarioId = input.ScenarioId!, Kind = input.Kind!,
                    DeviceTime = deviceTime!, ReceivedAt = received.ToString("o", CultureInfo.InvariantCulture),
                    ClockOffsetMs = request.ClockOffsetMs,
                    CorrectedTime = request.ClockOffsetMs is { } o
                        ? DateTimeOffset.Parse(deviceTime!, CultureInfo.InvariantCulture).AddMilliseconds(o)
                            .UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
                        : null,
                    Data = input.Data,
                };
                await AppendAsync(record, received, ct);
                var seenFile = paths.EdgeSeenFile(node.NodeId, id);
                await StoragePaths.AtomicWriteJsonAsync(seenFile, new SeenMark { ReceivedAt = record.ReceivedAt, Seq = record.Seq }, ct);
                // 정리는 파일의 수정 시각으로 나이를 센다 — 그 시각을 «받은 때» 로 못 박아 주입된 시계와 한 시계로 센다.
                File.SetLastWriteTimeUtc(seenFile, received.UtcDateTime);
                rangesChanged |= Add(ranges, record.Seq);
                await ForwardAsync(record, ct);
                results.Add(new EdgeEventResult { EventId = id, Status = "accepted" });
            }

            if (rangesChanged)
                await StoragePaths.AtomicWriteJsonAsync(paths.EdgeSeqFile(node.NodeId), new SeqFile { Ranges = ranges }, ct);
            SweepSeenIfDue(node.NodeId);
            var gaps = Holes(ranges);
            return new EdgeEventsResponse { Results = results, MissingSeq = gaps.Count > 0 ? gaps : null };
        }
        finally { gate.Release(); }
    }

    /// <summary>항목 하나의 형식 · 배정 검사 — 거절 코드 또는 null. 통과하면 <paramref name="deviceTime"/> 은 UTC 로 정규화된 값.</summary>
    private static string? Validate(EdgeEventInput e, EdgeNode node, out string? deviceTime)
    {
        deviceTime = null;
        if (e.EventId is null || !Uuid7.IsMatch(e.EventId)) return "invalid_event_id";
        if (e.Seq is null or < 0) return "invalid_seq";
        if (e.Kind is null || !KindPattern.IsMatch(e.Kind)) return "invalid_kind";
        if (e.ScenarioId is null) return "invalid_scenario";
        try { StoragePaths.Id(e.ScenarioId); }
        catch (ArgumentException) { return "invalid_scenario"; }
        if (string.Equals(e.ScenarioId, StoragePaths.EdgeSegment, StringComparison.OrdinalIgnoreCase)) return "invalid_scenario";
        if (!CaptureTime.TryNormalize(e.DeviceTime, out deviceTime) || deviceTime is null) return "invalid_time";
        if (e.Data is { } data)
        {
            if (data.ValueKind != JsonValueKind.Object) return "invalid_data";
            if (Encoding.UTF8.GetByteCount(data.GetRawText()) > MaxDataBytes) return "invalid_data";
        }
        return node.Scenarios.Contains(e.ScenarioId, StringComparer.Ordinal) ? null : "scenario_not_assigned";
    }

    private async Task AppendAsync(EdgeEventRecord record, DateTimeOffset received, CancellationToken ct)
    {
        var path = paths.EdgeEventsFile(record.NodeId, received.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Compact) + "\n");
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(bytes, ct);
    }

    /// <summary>기존 사건 내보내기 outbox 에 같은 봉투로 싣는다 — 받는 곳이 없으면 아무것도 쓰지 않고, 실패는 수신을 막지 않는다(degrade).</summary>
    private async Task ForwardAsync(EdgeEventRecord record, CancellationToken ct)
    {
        try
        {
            var envelope = new EdgeEventEnvelope
            {
                Id = record.EventId,
                Source = "/u-vision/edge/nodes/" + record.NodeId,
                Time = record.CorrectedTime ?? record.DeviceTime,
                Subject = record.ScenarioId,
                Data = record,
            };
            await publisher.PublishBodyAsync(record.ScenarioId, record.EventId, JsonSerializer.Serialize(envelope, Compact), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "엣지 사건 내보내기 outbox 쓰기 실패 — 수신은 완료 (node={NodeId}, event={EventId})", record.NodeId, record.EventId);
        }
    }

    // --- seq 구간 집합 -----------------------------------------------------------------------

    private sealed record SeenMark
    {
        [JsonPropertyName("received_at")] public required string ReceivedAt { get; init; }
        [JsonPropertyName("seq")] public required long Seq { get; init; }
    }

    private sealed record SeqFile
    {
        [JsonPropertyName("ranges")] public required List<long[]> Ranges { get; init; }
    }

    private async Task<List<long[]>> ReadRangesAsync(string nodeId, CancellationToken ct)
    {
        var path = paths.EdgeSeqFile(nodeId);
        if (!File.Exists(path))
            return [];
        try
        {
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<SeqFile>(stream, StoragePaths.Json, ct);
            return file?.Ranges.Where(r => r.Length == 2 && r[0] <= r[1]).ToList() ?? [];
        }
        catch (JsonException) { return []; } // 잘린 파일은 빈 집합 — 구멍이 과하게 보일 뿐 사건을 잃지 않는다.
    }

    /// <summary>번호 하나를 집합에 더한다(인접 구간은 합친다). 바뀌었으면 true.</summary>
    internal static bool Add(List<long[]> ranges, long seq)
    {
        for (var i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];
            if (seq >= r[0] && seq <= r[1]) return false;
            if (seq == r[1] + 1)
            {
                r[1] = seq;
                if (i + 1 < ranges.Count && ranges[i + 1][0] == seq + 1) { r[1] = ranges[i + 1][1]; ranges.RemoveAt(i + 1); }
                return true;
            }
            if (seq == r[0] - 1) { r[0] = seq; return true; }
            if (seq < r[0]) { ranges.Insert(i, [seq, seq]); return true; }
        }
        ranges.Add([seq, seq]);
        return true;
    }

    /// <summary>받은 가장 작은 번호부터 가장 큰 번호 사이의 빈 구간(앞쪽이 먼저, 최대 20개). 처음 받은 번호 앞은 구멍이 아니라 시작이다.</summary>
    internal static List<SeqRange> Holes(List<long[]> ranges)
    {
        var holes = new List<SeqRange>();
        for (var i = 1; i < ranges.Count && holes.Count < MaxReportedGaps; i++)
            holes.Add(new SeqRange { From = ranges[i - 1][1] + 1, To = ranges[i][0] - 1 });
        return holes;
    }

    // --- 지연 정리 ---------------------------------------------------------------------------

    /// <summary>
    /// 오래된 «받았다» 표지를 지운다 — 요청 경로에서 최대 한 시간에 한 번, 이 노드의 것만. 실패는 무시한다(표지는 작고 다음에 다시 시도).
    /// </summary>
    private void SweepSeenIfDue(string nodeId)
    {
        var now = clock.GetUtcNow();
        if (now - _lastSweep < SweepInterval)
            return;
        _lastSweep = now;
        try
        {
            var dir = paths.EdgeSeenDir(nodeId);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
                if (now - new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) > SeenRetention)
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "엣지 중복 표지 정리 실패 — 다음에 다시 (node={NodeId})", nodeId);
        }
    }
}
