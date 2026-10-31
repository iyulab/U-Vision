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

    /// <summary>
    /// 사람이 읽는 표시 이름(현장 말 — 한글 허용). id 는 학습 폴더 · 출력 enum · 라벨 키라 영숫자로 묶여 있으므로 화면 ·
    /// 프롬프트 · 내보내기는 이 이름을 쓰고 키는 id 그대로다. 비면 id 가 곧 이름이다.
    /// </summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

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

/// <summary>
/// 전용 ML 학습 데이터의 기준 — 비면 기본값(<see cref="Default"/>). 빌드 · 재빌드는 관리자가 하고, 이 값은 «빌드
/// 가능» · «재빌드 제안» 신호와 데이터셋 선정에만 쓴다.
/// </summary>
public sealed record ScenarioTraining
{
    /// <summary>클래스마다 있어야 할 사람 확정 라벨 수 — 모든 클래스가 닿아야 첫 빌드가 가능하다.</summary>
    [JsonPropertyName("min_per_class")] public int MinPerClass { get; init; }

    /// <summary>OK 표본 상한 — NG 쪽(불량 유형 전부) 건수의 몇 배까지 OK 를 넣는가. 희소한 NG 가 묻히지 않게.</summary>
    [JsonPropertyName("ok_ratio_cap")] public double OkRatioCap { get; init; }

    /// <summary>쓰는 모델의 데이터셋 이후 새로 확정된 라벨이 이만큼이면 재빌드를 제안한다.</summary>
    [JsonPropertyName("rebuild_min_new_labels")] public int RebuildMinNewLabels { get; init; }

    /// <summary>시작값 — 클래스당 30 · OK 는 NG 의 3배까지 · 새 라벨 30건이면 재빌드 제안. 시나리오가 덮어쓴다.</summary>
    public static readonly ScenarioTraining Default = new() { MinPerClass = 30, OkRatioCap = 3, RebuildMinNewLabels = 30 };
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

    [JsonPropertyName("training")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioTraining? Training { get; init; }
}

/// <summary>
/// 시나리오 v2 어휘 — 경계 입력은 이 목록으로 거부한다(변형하지 않는다). 어휘 전체가 아니라
/// <b>지금 지어진 것</b>만 받는다: 받는 값이 늘 때 이 목록이 함께 는다.
/// </summary>
public static class ScenarioVocabulary
{
    public const string Inspection = "inspection";
    public const string Verification = "verification";
    public const string Counting = "counting";
    public const string Still = "still";
    public const string MatchTier = "match";
    public const string MlTier = "ml";
    public const string MemoryTier = "memory";
    public const string VlmTier = "vlm";
    public const string DetectorTier = "detector";

    /// <summary>조각 = 시나리오 하나. 장부에 이미 쌓인 기록과 이어지도록 이름을 유지한다.</summary>
    public const string ScenarioSlice = "scenario-v1";

    public const string Ok = "OK";
    public const string Ng = "NG";
    public const string Match = "MATCH";
    public const string Mismatch = "MISMATCH";

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { Inspection, Verification, Counting };
    public static readonly IReadOnlySet<string> Triggers = new HashSet<string>(StringComparer.Ordinal) { Still };
    public static readonly IReadOnlySet<string> Tiers = new HashSet<string>(StringComparer.Ordinal) { MatchTier, MemoryTier, MlTier, VlmTier, DetectorTier };

    /// <summary>
    /// 시나리오 유형마다 쓸 수 있는 단 — 대조(<c>match</c>)는 기대값이 있는 확인에만, 전용 ML 분류기와 확정 사례 기억
    /// (<c>memory</c>)은 클래스가 이미지 자체로 정해지는 검사에만 뜻이 있다(확인의 일치/불일치는 기대 품번에 상대적이다).
    /// 계수는 검출기(<c>detector</c>) 한 단뿐이다 — VLM 은 세지 못한다.
    /// </summary>
    public static IReadOnlySet<string> TiersFor(string type) => type switch
    {
        Verification => VerificationTiers,
        Counting => CountingTiers,
        _ => InspectionTiers,
    };

    private static readonly IReadOnlySet<string> InspectionTiers = new HashSet<string>(StringComparer.Ordinal) { MemoryTier, MlTier, VlmTier };
    private static readonly IReadOnlySet<string> CountingTiers = new HashSet<string>(StringComparer.Ordinal) { DetectorTier };
    private static readonly IReadOnlySet<string> VerificationTiers = new HashSet<string>(StringComparer.Ordinal) { MatchTier, VlmTier };
    public static readonly IReadOnlySet<string> SliceKeys = new HashSet<string>(StringComparer.Ordinal) { ScenarioSlice };
}
