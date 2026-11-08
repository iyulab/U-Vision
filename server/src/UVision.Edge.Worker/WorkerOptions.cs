using System.Globalization;

namespace UVision.Edge.Worker;

public sealed class WorkerConfigException(IReadOnlyList<string> problems)
    : Exception("worker configuration problems: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// The worker's settings - all environment variables (container convention). An unknown or out-of-range value is rejected at start, all
/// problems at once: a worker that quietly runs on defaults is the hardest kind to find on a site.
/// </summary>
public sealed record WorkerOptions
{
    /// <summary>The agent's unix socket (an absolute path in the volume shared with the agent).</summary>
    public required string SocketPath { get; init; }

    /// <summary>
    /// The node-local file mapping a camera alias to its source: <c>{"dock-1": "/video/line.mp4"}</c> (a path or a stream URL). Only the worker
    /// reads it - a stream address can carry a password, so it never reaches the scenario, the agent or the engine.
    /// </summary>
    public required string CamerasFile { get; init; }

    /// <summary>The model server's base address (<c>mloop serve</c>).</summary>
    public required Uri DetectorUrl { get; init; }

    public string? DetectorToken { get; init; }

    /// <summary>Frames per second taken from the source. A CPU model server answers in about 0.4 s, so 2 is what real-time playback can keep up with.</summary>
    public double SampleFps { get; init; } = 2;

    public double ScoreThreshold { get; init; } = 0.3;

    /// <summary>Only these classes are reported (empty = all the model knows).</summary>
    public IReadOnlySet<string> Classes { get; init; } = new HashSet<string>();

    /// <summary>Start a recording over when it ends (a stand-in for a camera that never ends). Off by default: the end of a recording is reported as lost signal.</summary>
    public bool Loop { get; init; }

    public string Ffmpeg { get; init; } = "ffmpeg";

    /// <summary>Frames wider than this are scaled down before the model sees them.</summary>
    public int MaxWidth { get; init; } = 960;

    /// <summary>How often the cameras file and a running source are checked for change.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);

    public static WorkerOptions FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        var problems = new List<string>();
        string? Get(string key) => env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        string? Require(string key)
        {
            var v = Get(key);
            if (v is null) problems.Add($"{key} is required");
            return v;
        }
        double Number(string key, double fallback, double min, double max)
        {
            var raw = Get(key);
            if (raw is null) return fallback;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max)
            {
                problems.Add($"{key} must be a number from {min} to {max}");
                return fallback;
            }
            return n;
        }

        var socket = Require("UVISION_WORKER_SOCKET");
        if (socket is not null && !Path.IsPathRooted(socket)) problems.Add("UVISION_WORKER_SOCKET must be an absolute path");
        var cameras = Require("UVISION_CAMERAS_FILE");

        Uri? detector = null;
        if (Require("UVISION_DETECTOR_URL") is { } rawUrl)
        {
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out detector) || detector.Scheme is not ("http" or "https"))
            {
                problems.Add("UVISION_DETECTOR_URL must be an http(s) address");
                detector = null;
            }
        }

        var token = Get("UVISION_DETECTOR_TOKEN");
        if (Get("UVISION_DETECTOR_TOKEN_FILE") is { } tokenFile)
        {
            try { token = File.ReadAllText(tokenFile).Trim(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add($"UVISION_DETECTOR_TOKEN_FILE cannot be read: {e.Message}"); }
        }

        var loop = false;
        if (Get("UVISION_VIDEO_LOOP") is { } rawLoop && !bool.TryParse(rawLoop, out loop)) problems.Add("UVISION_VIDEO_LOOP must be true or false");

        var fps = Number("UVISION_SAMPLE_FPS", 2, 0.1, 30);
        var threshold = Number("UVISION_SCORE_THRESHOLD", 0.3, 0, 1);
        var width = (int)Number("UVISION_MAX_WIDTH", 960, 64, 7680);
        var poll = Number("UVISION_POLL_SECONDS", 2, 0.2, 3600);
        var classes = (Get("UVISION_CLASSES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

        if (problems.Count > 0) throw new WorkerConfigException(problems);
        return new WorkerOptions
        {
            SocketPath = socket!, CamerasFile = cameras!, DetectorUrl = detector!, DetectorToken = token,
            SampleFps = fps, ScoreThreshold = threshold, Classes = classes, Loop = loop,
            Ffmpeg = Get("UVISION_FFMPEG") ?? "ffmpeg", MaxWidth = width, Poll = TimeSpan.FromSeconds(poll),
        };
    }
}
