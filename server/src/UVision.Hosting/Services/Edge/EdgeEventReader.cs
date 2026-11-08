using System.Globalization;
using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Services.Events;
using UVision.Api.Storage;

namespace UVision.Api.Services.Edge;

/// <summary>
/// 노드의 사건 장부(<c>edge/nodes/{node}/events/{date}.jsonl</c>)를 최신부터 읽는다 — 관리자가 노드가 보낸 것을 보는 읽기 전용 면.
/// 장부는 수신 순서로 덧붙는 파일이라 날짜 파일을 내림차순으로, 파일 안은 뒤에서 앞으로 읽는다. 쓰는 쪽이 열어 둔 파일도 읽는다(공유 읽기).
/// </summary>
public sealed class EdgeEventReader(StoragePaths paths)
{
    /// <summary>한 번에 읽는 사건 수의 상한 — 파생 «지금 상태» 의 범위이기도 하다. 상한에 걸리면 <c>truncated</c> 로 알린다.</summary>
    public const int MaxScan = 5000;

    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <exception cref="ArgumentException">노드 id 형식 위반 · <paramref name="limit"/> 가 범위 밖 — 호출부가 400 으로.</exception>
    public async Task<EdgeNodeEventsView> ReadAsync(string nodeId, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > MaxLimit)
            throw new ArgumentException($"limit 는 1~{MaxLimit}");

        var scanned = new List<EdgeEventRecord>();
        var truncated = false;
        var dir = Path.GetDirectoryName(paths.EdgeEventsFile(nodeId, "2000-01-01"))!;
        if (Directory.Exists(dir))
        {
            var files = Directory.EnumerateFiles(dir, "*.jsonl")
                .Where(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var lines = await ReadLinesAsync(file, ct);
                for (var i = lines.Count - 1; i >= 0; i--)
                {
                    if (scanned.Count >= MaxScan) { truncated = true; break; }
                    // 잘린 줄(쓰다 멈춤) · 깨진 줄은 건너뛴다 — 장부는 읽기 면이 고치지 않는다.
                    try
                    {
                        if (JsonSerializer.Deserialize<EdgeEventRecord>(lines[i], StoragePaths.Json) is { } record)
                            scanned.Add(record);
                    }
                    catch (JsonException) { }
                }
                if (truncated) break;
            }
        }

        return new EdgeNodeEventsView
        {
            NodeId = nodeId,
            Events = scanned.Take(limit).ToList(),
            Current = EdgeCurrentStateBuilder.Build(scanned),
            Scanned = scanned.Count,
            Truncated = truncated,
        };
    }

    private static async Task<List<string>> ReadLinesAsync(string path, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var stream = await StoragePaths.OpenReadResilientAsync(path, ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.Length > 0) lines.Add(line);
        return lines;
    }
}

/// <summary>
/// 사건에서 «지금 상태» 를 뽑는 순수 계산 — 규칙(또는 소스)마다 <b>가장 큰 <c>seq</c></b> 의 사건이 마지막이다(도착 순서가 아니라 노드의 번호로).
/// 입력은 최신부터 순서대로이지만 순서에 기대지 않는다.
/// </summary>
public static class EdgeCurrentStateBuilder
{
    public static EdgeCurrentState Build(IReadOnlyList<EdgeEventRecord> events)
    {
        var counts = new Dictionary<(string, string), EdgeEventRecord>();
        var alarms = new Dictionary<(string, string), EdgeEventRecord>();
        var sources = new Dictionary<(string, string), EdgeEventRecord>();

        foreach (var e in events)
        {
            if (e.Data is not { ValueKind: JsonValueKind.Object } data) continue;
            switch (e.Kind)
            {
                case "count_changed" when data.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number
                                          && Str(data, "rule") is { } rule:
                    Keep(counts, (e.ScenarioId, rule), e);
                    break;
                case "alarm_raised" or "alarm_cleared" when Str(data, "rule") is { } alarmRule:
                    Keep(alarms, (e.ScenarioId, alarmRule), e);
                    break;
                case "source_state" when Str(data, "state") is not null:
                    Keep(sources, (e.ScenarioId, Str(data, "camera") ?? Str(data, "source") ?? ""), e);
                    break;
            }
        }

        return new EdgeCurrentState
        {
            Counts = counts.Values.OrderBy(e => e.ScenarioId, StringComparer.Ordinal).ThenBy(e => Str(e.Data!.Value, "rule"), StringComparer.Ordinal)
                .Select(ToCount).ToList(),
            Alarms = alarms.Values.OrderBy(e => e.ScenarioId, StringComparer.Ordinal).ThenBy(e => Str(e.Data!.Value, "rule"), StringComparer.Ordinal)
                .Select(ToAlarm).ToList(),
            Sources = sources.Values.OrderBy(e => e.ScenarioId, StringComparer.Ordinal).ThenBy(e => Str(e.Data!.Value, "camera"), StringComparer.Ordinal)
                .Select(ToSource).ToList(),
        };
    }

    private static void Keep(Dictionary<(string, string), EdgeEventRecord> map, (string, string) key, EdgeEventRecord e)
    {
        if (!map.TryGetValue(key, out var held) || e.Seq > held.Seq)
            map[key] = e;
    }

    private static string? Str(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static EdgeCurrentCount ToCount(EdgeEventRecord e)
    {
        var d = e.Data!.Value;
        return new EdgeCurrentCount
        {
            ScenarioId = e.ScenarioId, Rule = Str(d, "rule")!, Source = Str(d, "source"), Zone = Str(d, "zone"), Class = Str(d, "class"),
            Count = d.GetProperty("count").GetInt32(), Since = Str(d, "since"),
            DeviceTime = e.DeviceTime, ReceivedAt = e.ReceivedAt, ConfigStale = Flag(d, "config_stale"),
        };
    }

    private static EdgeCurrentAlarm ToAlarm(EdgeEventRecord e)
    {
        var d = e.Data!.Value;
        return new EdgeCurrentAlarm
        {
            ScenarioId = e.ScenarioId, Rule = Str(d, "rule")!, Source = Str(d, "source"), Zone = Str(d, "zone"), Class = Str(d, "class"),
            State = e.Kind == "alarm_raised" ? "raised" : "cleared",
            Count = d.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null,
            Since = Str(d, "since"), DeviceTime = e.DeviceTime, ReceivedAt = e.ReceivedAt, ConfigStale = Flag(d, "config_stale"),
        };
    }

    private static EdgeCurrentSource ToSource(EdgeEventRecord e)
    {
        var d = e.Data!.Value;
        return new EdgeCurrentSource
        {
            ScenarioId = e.ScenarioId, Source = Str(d, "source"), Camera = Str(d, "camera"), State = Str(d, "state")!,
            DeviceTime = e.DeviceTime, ReceivedAt = e.ReceivedAt,
        };
    }
}
