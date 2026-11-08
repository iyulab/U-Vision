using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UVision.Edge.Worker;

/// <summary>A box in frame-relative coordinates (0..1, origin top-left): <c>X, Y</c> is the top-left corner.</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Area => W * H;

    /// <summary>Intersection over union with another box; 0 when they do not overlap.</summary>
    public double Iou(Box other)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(X + W, other.X + other.W);
        var y2 = Math.Min(Y + H, other.Y + other.H);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }
}

/// <summary>One model detection before tracking.</summary>
public sealed record Detection(string Label, double Score, Box Box);

/// <summary>A tracked object in one frame - the id stays the same while the object is followed across frames.</summary>
public sealed record Track(string Id, string Class, double Score, Box Box);

/// <summary>
/// The worker-to-agent line protocol, version 1. One JSON object per line, LF-terminated. The time carries a zone (the agent rejects a
/// zone-less time rather than guess a wall clock); boxes are <c>[x, y, w, h]</c> in 0..1.
/// </summary>
public static class Wire
{
    public const int Version = 1;

    public static string TracksLine(string cameraAlias, long seq, DateTimeOffset time, IReadOnlyList<Track> tracks)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("type", "tracks");
            w.WriteString("source_id", cameraAlias);
            w.WriteNumber("seq", seq);
            w.WriteString("t", Iso(time));
            w.WritePropertyName("tracks");
            w.WriteStartArray();
            foreach (var t in tracks)
            {
                w.WriteStartObject();
                w.WriteString("id", t.Id);
                w.WriteString("class", t.Class);
                w.WriteNumber("score", Math.Round(t.Score, 4));
                w.WritePropertyName("box");
                w.WriteStartArray();
                w.WriteNumberValue(Math.Round(t.Box.X, 5));
                w.WriteNumberValue(Math.Round(t.Box.Y, 5));
                w.WriteNumberValue(Math.Round(t.Box.W, 5));
                w.WriteNumberValue(Math.Round(t.Box.H, 5));
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    public static string SourceStateLine(string cameraAlias, string state, DateTimeOffset time)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("type", "source_state");
            w.WriteString("source_id", cameraAlias);
            w.WriteString("state", state);
            w.WriteString("t", Iso(time));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>ISO 8601 with a zone - always UTC, so the string never depends on the machine's time zone.</summary>
    public static string Iso(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>The three source states the agent understands.</summary>
public static class CameraState
{
    public const string Ok = "ok";
    public const string NoSignal = "no_signal";
    public const string Degraded = "degraded";
}
