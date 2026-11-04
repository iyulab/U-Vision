using System.Globalization;
using System.Text.Json;
using UVision.Edge.Agent.Rules;

namespace UVision.Edge.Agent.Worker;

/// <summary>
/// 워커 → 에이전트 줄 프로토콜(원장 D-105 W-1 · W-2) — 한 줄이 JSON 객체 하나(NDJSON). 워커는 <b>보기만</b> 하고 판단하지 않는다.
/// <code>
/// {"v":1,"type":"tracks","source_id":"dock-1","seq":42,"t":"2026-11-04T09:00:00.123+00:00",
///  "tracks":[{"id":"7","class":"box","score":0.91,"box":[0.12,0.30,0.10,0.18]}]}
/// {"v":1,"type":"source_state","source_id":"dock-1","state":"no_signal","t":"2026-11-04T09:00:05+00:00"}
/// </code>
/// <c>source_id</c> 는 <b>노드 로컬 카메라 별칭</b>(시나리오 <c>camera_ref</c> 와 같은 이름)이고 <c>t</c> 는 프레임이 찍힌 벽시계 시각(시간대 필수)이다.
/// 상자 <c>box</c> 는 <c>[x, y, w, h]</c> — 프레임 기준 0~1, 왼쪽 위 원점. 모르는 판 · 종류 · 깨진 줄은 <b>버리고 센다</b>(워커를 멈추지 않는다).
/// </summary>
public static class WorkerProtocol
{
    /// <summary>지금 읽는 프로토콜 판.</summary>
    public const int Version = 1;

    /// <summary>한 줄의 최대 길이(바이트) — 넘으면 줄 전체를 버린다.</summary>
    public const int MaxLineBytes = 1 << 20;

    /// <summary>프레임 하나의 최대 트랙 수.</summary>
    public const int MaxTracks = 2000;

    private const int MaxIdLength = 64;

    /// <summary>한 줄을 읽는다. 성공이면 <paramref name="message"/> 가 <see cref="TrackFrame"/> 또는 <see cref="SourceStateMessage"/>, 아니면 버린 사유.</summary>
    public static bool TryParse(ReadOnlySpan<byte> line, out object? message, out string? reason)
    {
        message = null;
        reason = null;
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("객체가 아니다", out reason);
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != Version)
                return Fail($"모르는 프로토콜 판(지원: {Version})", out reason);
            var type = Text(root, "type");
            var camera = Text(root, "source_id");
            if (camera is not { Length: > 0 and <= MaxIdLength }) return Fail("source_id 가 없거나 너무 길다", out reason);
            if (!TryTime(root, out var time)) return Fail("t 가 시간대가 있는 ISO 8601 시각이 아니다", out reason);

            switch (type)
            {
                case "tracks":
                    return TryFrame(root, camera, time, out message, out reason);
                case "source_state":
                    var state = Text(root, "state");
                    if (state is null || !SourceStates.All.Contains(state)) return Fail("state 는 ok | no_signal | degraded", out reason);
                    message = new SourceStateMessage(camera, state, time);
                    return true;
                default:
                    return Fail($"모르는 type: {type}", out reason);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // JsonDocument 의 접근자는 모양이 다르면 던진다 — 워커가 보낸 줄 하나가 수신을 죽이지 못하게 마지막 방어선.
            return Fail("JSON 이 아니거나 모양이 맞지 않는다", out reason);
        }
    }

    private static bool TryFrame(JsonElement root, string camera, DateTimeOffset time, out object? message, out string? reason)
    {
        message = null;
        long seq = 0;
        if (root.TryGetProperty("seq", out var s) && (s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out seq) || seq < 0)) return Fail("seq 는 0 이상의 정수", out reason);
        if (!root.TryGetProperty("tracks", out var list) || list.ValueKind != JsonValueKind.Array) return Fail("tracks 가 배열이 아니다", out reason);
        if (list.GetArrayLength() > MaxTracks) return Fail($"tracks 가 {MaxTracks}개를 넘는다", out reason);

        var tracks = new List<TrackSample>(list.GetArrayLength());
        foreach (var t in list.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) return Fail("tracks[] 가 객체가 아니다", out reason);
            var id = t.TryGetProperty("id", out var idValue)
                ? idValue.ValueKind switch
                {
                    JsonValueKind.String => idValue.GetString(),
                    JsonValueKind.Number => idValue.GetRawText(),
                    _ => null,
                }
                : null;
            var cls = Text(t, "class");
            if (id is not { Length: > 0 and <= MaxIdLength } || cls is not { Length: > 0 and <= 64 }) return Fail("tracks[].id · class 가 없거나 잘못됐다", out reason);
            if (!t.TryGetProperty("score", out var sc) || sc.ValueKind != JsonValueKind.Number || !sc.TryGetDouble(out var score) || !double.IsFinite(score)) return Fail("tracks[].score 가 수가 아니다", out reason);
            if (!t.TryGetProperty("box", out var box) || box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4) return Fail("tracks[].box 는 [x, y, w, h]", out reason);
            var b = new double[4];
            var i = 0;
            foreach (var n in box.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out b[i]) || !double.IsFinite(b[i])) return Fail("tracks[].box 의 값이 수가 아니다", out reason);
                i++;
            }
            if (b[2] < 0 || b[3] < 0) return Fail("tracks[].box 의 w · h 는 0 이상", out reason);
            tracks.Add(new TrackSample(id, cls, score, b[0], b[1], b[2], b[3]));
        }

        message = new TrackFrame(camera, seq, time, tracks);
        reason = null;
        return true;
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool TryTime(JsonElement root, out DateTimeOffset time)
    {
        time = default;
        // 시간대 없는 시각은 거부한다 — 벽시계를 추측해 규칙의 지속 시간을 틀리게 하지 않는다.
        // RoundtripKind 는 «Z» 를 Utc, «±hh:mm» 를 Local 로 읽고 시간대가 없으면 Unspecified 로 남긴다.
        if (Text(root, "t") is not { } text
            || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            || parsed.Kind == DateTimeKind.Unspecified)
            return false;
        time = new DateTimeOffset(parsed);
        return true;
    }

    private static bool Fail(string why, out string? reason)
    {
        reason = why;
        return false;
    }
}
