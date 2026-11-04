using System.Text.Json.Serialization;

namespace UVision.Edge.Agent.Worker;

/// <summary>
/// 상태 보고에 싣는 추론 워커 수신 상태(엔진의 <c>EdgeWorkerStatus</c> 와 같은 모양) — 운영자가 콘솔에서 «워커가 안 붙었다 · 깨진 줄을
/// 보낸다 · 규칙이 건너뛰어졌다» 를 보게 한다.
/// </summary>
public sealed record WorkerStatusReport(
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("last_message_at")] string? LastMessageAt,
    [property: JsonPropertyName("frames")] long Frames,
    [property: JsonPropertyName("dropped")] long Dropped,
    [property: JsonPropertyName("last_drop_reason")] string? LastDropReason,
    [property: JsonPropertyName("rules")] int Rules,
    [property: JsonPropertyName("skipped_rules")] int SkippedRules,
    [property: JsonPropertyName("skipped")] IReadOnlyList<string>? Skipped,
    [property: JsonPropertyName("config_stale")] bool ConfigStale = false)
{
    /// <summary>수신 통계와 규칙 상태에서 보고를 만든다 — 사유 · 건너뜀 목록은 엔진의 한도(200자 · 10개)에 맞춰 자른다.</summary>
    public static WorkerStatusReport From(WorkerSocketStats stats, RuleRunner runner, bool configStale = false)
    {
        var skipped = runner.Skipped;
        return new WorkerStatusReport(
            stats.Connected,
            stats.LastMessageAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            stats.Frames, stats.Dropped,
            Clip(stats.LastDropReason),
            runner.RuleCount, skipped.Count,
            skipped.Count == 0 ? null : [.. skipped.Take(10).Select(s => Clip(s) ?? "")],
            configStale);
    }

    private static string? Clip(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new string(text.Where(c => !char.IsControl(c)).Take(200).ToArray()).Trim();
}
