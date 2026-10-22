using System.Text.Json.Serialization;
using UVision.Api.Models;

namespace UVision.Api.Services.Events;

/// <summary>
/// 판정 사건 한 건 — CloudEvents 1.0 JSON 봉투에 결과 v2(<see cref="InspectResultV2"/>, 이미 공개된 모양)를
/// <c>data</c> 로 싣는다. 새 스키마를 만들지 않고 표준 봉투 + 기존 계약을 쓴다.
/// </summary>
public sealed record DecisionEvent
{
    public const string DecidedType = "u-vision.inspection.decided";

    [JsonPropertyName("specversion")] public string SpecVersion { get; init; } = "1.0";
    /// <summary>판정 id(= 결과 v2 <c>decision_id</c>) — 받는 쪽이 중복 수신을 거르는 키.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = DecidedType;
    [JsonPropertyName("time")] public required string Time { get; init; }
    [JsonPropertyName("subject")] public required string Subject { get; init; }
    [JsonPropertyName("datacontenttype")] public string DataContentType { get; init; } = "application/json";
    [JsonPropertyName("data")] public required InspectResultV2 Data { get; init; }

    public static DecisionEvent From(InspectResultV2 decision) => new()
    {
        Id = decision.DecisionId,
        Source = "/u-vision/scenarios/" + decision.ScenarioId,
        Time = decision.Timestamp,
        Subject = decision.DecisionId,
        Data = decision,
    };
}

/// <summary>outbox 파일 하나 — 보낼 본문(서명이 덮는 바이트 그대로)과 보내기 상태.</summary>
public sealed record OutboxEntry
{
    [JsonPropertyName("event_id")] public required string EventId { get; init; }
    [JsonPropertyName("body")] public required string Body { get; init; }
    [JsonPropertyName("attempts")] public int Attempts { get; init; }
    [JsonPropertyName("next_attempt_at")] public string? NextAttemptAt { get; init; }
    [JsonPropertyName("last_error")] public string? LastError { get; init; }
}
