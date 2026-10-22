using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 시나리오 v2 의 클래스 하나 — 시나리오가 내놓을 수 있는 판정 값과 그 값의 위험 등급.
/// <see cref="Settleable"/> 은 아래 단(system 1 · ML)이 확정해도 되는 값, <see cref="MustCatch"/> 는 놓치면 안 되어
/// 언제나 꼭대기(VLM)로 올라가는 값이다. 둘 다 아니면 확정은 꼭대기만 한다.
/// </summary>
public sealed record ScenarioClass
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("settleable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Settleable { get; init; }

    [JsonPropertyName("must_catch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MustCatch { get; init; }
}

/// <summary>
/// 판정 단 하나 — 라우터가 비용 순(배열 순서)으로 묻는다. 마지막 단이 꼭대기(답하면 확정)다.
/// </summary>
public sealed record ScenarioJudge
{
    /// <summary>단 이름 — <see cref="ScenarioVocabulary.Tiers"/> 중 하나.</summary>
    [JsonPropertyName("tier")] public required string Tier { get; init; }
}

/// <summary>관문 목표값 — 비면 기본값(<see cref="ScenarioTargets.Default"/>).</summary>
public sealed record ScenarioTargets
{
    /// <summary>p0 — 아래 단과 꼭대기의 일치 목표.</summary>
    [JsonPropertyName("agreement")] public double Agreement { get; init; }

    /// <summary>m — 꼭 잡을 값의 누락 상한.</summary>
    [JsonPropertyName("max_miss_rate")] public double MaxMissRate { get; init; }

    /// <summary>N — 관문을 한 번 판정할 사전 등록 표본 수.</summary>
    [JsonPropertyName("sample_size")] public int SampleSize { get; init; }

    /// <summary>시작값 — 일치 ≥ 0.95 · 누락 ≤ 2% · 표본 150. 시나리오가 덮어쓴다.</summary>
    public static readonly ScenarioTargets Default = new() { Agreement = 0.95, MaxMissRate = 0.02, SampleSize = 150 };
}

/// <summary>신뢰 생애의 단위와 목표 — HoneAI 어댑터 입력.</summary>
public sealed record ScenarioTrust
{
    /// <summary>조각 키 체계 — <see cref="ScenarioVocabulary.SliceKeys"/> 중 하나. 비면 시나리오 하나가 조각.</summary>
    [JsonPropertyName("slice_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SliceKey { get; init; }

    [JsonPropertyName("targets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioTargets? Targets { get; init; }
}

/// <summary>
/// 시나리오 v2 어휘 — 경계 입력은 이 목록으로 거부한다(변형하지 않는다). 어휘 전체가 아니라
/// <b>지금 지어진 것</b>만 받는다: 받는 값이 늘 때 이 목록이 함께 는다.
/// </summary>
public static class ScenarioVocabulary
{
    public const string Inspection = "inspection";
    public const string Still = "still";
    public const string MlTier = "ml";
    public const string VlmTier = "vlm";

    /// <summary>조각 = 시나리오 하나. 장부에 이미 쌓인 기록과 이어지도록 이름을 유지한다.</summary>
    public const string ScenarioSlice = "scenario-v1";

    public const string Ok = "OK";
    public const string Ng = "NG";

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { Inspection };
    public static readonly IReadOnlySet<string> Triggers = new HashSet<string>(StringComparer.Ordinal) { Still };
    public static readonly IReadOnlySet<string> Tiers = new HashSet<string>(StringComparer.Ordinal) { MlTier, VlmTier };
    public static readonly IReadOnlySet<string> SliceKeys = new HashSet<string>(StringComparer.Ordinal) { ScenarioSlice };
}
