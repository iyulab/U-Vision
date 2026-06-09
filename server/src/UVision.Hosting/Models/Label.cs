using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 디스크에 영속화되는 사람 라벨 — <c>{image_id}.label.json</c> 의 스키마.
/// 불변 VLM 레코드(<see cref="StoredResult"/>)와 분리된 가변 사이드카. 정정/삭제 가능.
/// <para>
/// label 은 <b>string</b>(열린 클래스 식별자) — <see cref="Verdict"/> enum 을 재사용하지 않는다.
/// 현재 값은 "OK"/"NG"(<see cref="LabelSet"/>)지만 다중분류는 허용 값만 늘면 되고 스키마 변경이 없다.
/// </para>
/// </summary>
public sealed record StoredLabel
{
    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    [JsonPropertyName("label")] public required string Label { get; init; }

    /// <summary>라벨 기록(또는 정정) 시각. ISO-8601 UTC. 결과의 날짜 버킷과는 별개.</summary>
    [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }
}

/// <summary>
/// 허용 라벨 집합 — <b>이진→다중분류 확장의 단일 seam</b>.
/// v1: {"OK","NG"} 상수. 다중분류 확장 시 이 소스만 시나리오 구성으로 교체한다
/// (저장소·UI 렌더·일치 비교 로직은 불변).
/// </summary>
public static class LabelSet
{
    public static readonly IReadOnlySet<string> Default =
        new HashSet<string>(StringComparer.Ordinal) { "OK", "NG" };

    public static bool IsValid(string label) =>
        !string.IsNullOrEmpty(label) && Default.Contains(label);
}

/// <summary><c>PUT {api}/results/label</c> 요청 본문(snake_case wire).</summary>
public sealed record LabelInput
{
    [JsonPropertyName("scenario_id")] public string ScenarioId { get; init; } = "";

    [JsonPropertyName("date")] public string Date { get; init; } = "";

    [JsonPropertyName("image_id")] public string ImageId { get; init; } = "";

    [JsonPropertyName("label")] public string Label { get; init; } = "";
}
