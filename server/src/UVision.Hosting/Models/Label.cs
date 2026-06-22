using System.Text.Json.Serialization;
using UVision.Api.Services.Label;

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

    /// <summary>
    /// append-only 이벤트 이력(C1 provenance) — 운영 라벨·블라인드 감사 재라벨의 시간순 로그.
    /// 없으면(구 사이드카) <see cref="Normalized"/> 가 단일 label 이벤트로 합성한다(하위호환).
    /// </summary>
    [JsonPropertyName("history")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LabelEvent>? History { get; init; }

    /// <summary>감사 상태(C1) — 블라인드 재라벨과 operative 라벨의 일관성. 없으면 unaudited.</summary>
    [JsonPropertyName("audit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LabelAudit? Audit { get; init; }

    /// <summary>
    /// 구 사이드카(history 없음)를 단일 <see cref="LabelMode.Label"/> 이벤트로 합성(하위호환 읽기).
    /// 이미 history 가 있으면 그대로 반환한다. 디스크에 쓰지 않는 순수 in-memory 변환.
    /// </summary>
    public StoredLabel Normalized() =>
        History is { Count: > 0 }
            ? this
            : this with
            {
                History = [new LabelEvent { Label = Label, By = "", At = Timestamp, Mode = LabelMode.Label }],
                Audit = Audit ?? new LabelAudit { Status = LabelAuditStatus.Unaudited },
            };
}

/// <summary>라벨 이력의 단위 이벤트(C1 provenance) — append-only.</summary>
public sealed record LabelEvent
{
    [JsonPropertyName("label")] public required string Label { get; init; }

    /// <summary>라벨러 식별 = device UUID(cycle-35 재사용, D4). 구 이벤트/불가 시 "".</summary>
    [JsonPropertyName("by")] public required string By { get; init; }

    /// <summary>이벤트 시각. ISO-8601 UTC.</summary>
    [JsonPropertyName("at")] public required string At { get; init; }

    /// <summary><see cref="LabelMode"/> — label/audit(/oracle 미구현).</summary>
    [JsonPropertyName("mode")] public required string Mode { get; init; }
}

/// <summary>감사 상태 요약(C1) — <see cref="LabelAuditStatus"/>.</summary>
public sealed record LabelAudit
{
    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>최근 감사 평가 시각. unaudited 면 null.</summary>
    [JsonPropertyName("at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? At { get; init; }
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
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("date")] public required string Date { get; init; }

    [JsonPropertyName("image_id")] public required string ImageId { get; init; }

    [JsonPropertyName("label")] public required string Label { get; init; }

    /// <summary>라벨러 식별 = device UUID(D4). 선택적 — 누락 시 "".</summary>
    [JsonPropertyName("by")] public string? By { get; init; }
}
