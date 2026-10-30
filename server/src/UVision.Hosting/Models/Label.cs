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

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

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
    /// operative(사람) 라벨 = history 의 최신 <see cref="LabelMode.Label"/> 이벤트.
    /// 없으면 flat <see cref="Label"/>(구 사이드카), 둘 다 없으면 null(oracle-only 또는 미라벨).
    /// 표·검토큐·감사는 이 값을 GT 로 읽는다(oracle 이벤트는 operative 가 아니다 — D2 위계).
    /// </summary>
    [JsonIgnore]
    public string? OperativeLabel =>
        History?.LastOrDefault(e => e.Mode == LabelMode.Label)?.Label ?? Label;

    /// <summary>
    /// 오라클(④-B)의 최신 2차 소견 = history 의 최신 <see cref="LabelMode.Oracle"/> 이벤트.
    /// 없으면 null. operative 가 아니다(D2 위계) — <see cref="OperativeLabel"/> 과 대조해
    /// 사람 라벨러의 계통편향(systematic bias)을 flag-only 로 관측한다(메트릭 집계 입력).
    /// </summary>
    /// <summary>operative 라벨의 출제 흐름 표식(<see cref="LabelStream"/>) — 없으면 null(표식 없는 라벨).</summary>
    [JsonIgnore]
    public string? OperativeStream =>
        History?.LastOrDefault(e => e.Mode == LabelMode.Label)?.Stream;

    /// <summary>
    /// operative 라벨이 학습용 출제(<see cref="LabelStream.Learn"/>)에서 왔는가 — 맞으면 측정(재현율 · 격상 신호 · 감사 일관성)이
    /// 읽지 않는다. 표식이 없는 라벨은 측정용으로 읽는다.
    /// </summary>
    [JsonIgnore]
    public bool IsLearnStream => OperativeStream == LabelStream.Learn;

    /// <summary>
    /// operative 라벨이 시험 데이터셋의 정답인가 — 라벨러가 <see cref="CaptureOrigin.LabelerPrefix"/> 로 시작한다(주입 도구가
    /// 정답을 심을 때 쓰는 표기). 블라인드 감사는 사람 라벨의 일관성을 재는 것이라 이 라벨은 감사 표본에서 빠진다.
    /// </summary>
    [JsonIgnore]
    public bool IsDatasetLabel =>
        History?.LastOrDefault(e => e.Mode == LabelMode.Label)?.By.StartsWith(CaptureOrigin.LabelerPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// 오라클(④-B)의 최신 2차 소견 = history 의 최신 <see cref="LabelMode.Oracle"/> 이벤트.
    /// 없으면 null. operative 가 아니다(D2 위계) — <see cref="OperativeLabel"/> 과 대조해
    /// 사람 라벨러의 계통편향(systematic bias)을 flag-only 로 관측한다(메트릭 집계 입력).
    /// </summary>
    [JsonIgnore]
    public string? LatestOracleOpinion =>
        History?.LastOrDefault(e => e.Mode == LabelMode.Oracle)?.Label;

    /// <summary>
    /// 구 사이드카(history 없음)를 단일 <see cref="LabelMode.Label"/> 이벤트로 합성(하위호환 읽기).
    /// 이미 history 가 있으면 그대로 반환한다. 디스크에 쓰지 않는 순수 in-memory 변환.
    /// label=null+history 없음은 합성하지 않는다(oracle-only 또는 미라벨 — 이론상 미발생).
    /// </summary>
    public StoredLabel Normalized() =>
        History is { Count: > 0 }
            ? this
            : Label is not null
                ? this with
                {
                    History = [new LabelEvent { Label = Label, By = "", At = Timestamp, Mode = LabelMode.Label }],
                    Audit = Audit ?? new LabelAudit { Status = LabelAuditStatus.Unaudited },
                }
                : this; // label·history 둘 다 없음(이론상 미발생) — 합성 안 함.
}

/// <summary>라벨 이력의 단위 이벤트(C1 provenance) — append-only.</summary>
public sealed record LabelEvent
{
    [JsonPropertyName("label")] public required string Label { get; init; }

    /// <summary>라벨러 식별 = 장치 UUID(촬영 기록과 같은 값). 구 이벤트/불가 시 "".</summary>
    [JsonPropertyName("by")] public required string By { get; init; }

    /// <summary>이벤트 시각. ISO-8601 UTC.</summary>
    [JsonPropertyName("at")] public required string At { get; init; }

    /// <summary><see cref="LabelMode"/> — label(operative)/audit(블라인드)/oracle(④-B 2차의견·비operative).</summary>
    [JsonPropertyName("mode")] public required string Mode { get; init; }

    /// <summary>
    /// 이 라벨을 낳은 출제 흐름(<see cref="LabelStream"/>) — 측정 지표가 학습용 라벨을 읽지 않게 하는 표식. 없으면 null
    /// (이전 이벤트 · 운영 화면의 라벨 — 측정용으로 읽는다). 쓰지 않을 땐 직렬화에서 빠져 이전 사이드카와 byte 같다.
    /// </summary>
    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stream { get; init; }
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
/// 허용 라벨 집합 — 시나리오가 정한다(<see cref="Services.Scenarios.EffectiveScenario.Labels"/>: 검사는 클래스 id,
/// 오투입 확인은 OK/NG). 시나리오를 찾을 수 없으면(삭제된 시나리오의 지난 결과) 이분 판정의 <see cref="Default"/>.
/// 저장소 · 일치 비교는 라벨을 문자열로만 다루므로 이 집합만 바뀐다.
/// </summary>
public static class LabelSet
{
    public static readonly IReadOnlyList<string> Default = ["OK", "NG"];

    /// <summary>철자 그대로(대소문자 구분) — 라벨은 학습 폴더 이름 · 일치 비교의 키가 된다.</summary>
    public static bool IsValid(string label, IReadOnlyList<string> allowed) =>
        !string.IsNullOrEmpty(label) && allowed.Contains(label, StringComparer.Ordinal);
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

/// <summary><c>POST {api}/results/audit</c> 블라인드 재라벨 제출 본문(C1).</summary>
public sealed record AuditInput
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }
    [JsonPropertyName("date")] public required string Date { get; init; }
    [JsonPropertyName("image_id")] public required string ImageId { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("by")] public string? By { get; init; }
}
