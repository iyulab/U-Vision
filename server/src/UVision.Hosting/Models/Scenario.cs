using System.Text.Json.Serialization;

namespace UVision.Api.Models;

/// <summary>
/// 관심 영역 — **상대 좌표(0~1)**. 캡처/감지가 집중할 사각 구역.
/// 해상도 독립(태블릿마다 카메라 해상도가 다르므로): 클라가 같은 ROI 표현을 쓰고
/// 변환은 필드명 매핑(w↔width)뿐. 0 폭/높이는 "전체 프레임"을 뜻한다(미설정 기본).
/// (처음부터 상대좌표로 저장 — 기기 해상도와 무관하게 같은 영역을 가리킨다)
/// </summary>
public sealed record Roi
{
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("w")] public double W { get; init; }
    [JsonPropertyName("h")] public double H { get; init; }
}

/// <summary>
/// 시나리오 정의 — <c>scenario.json</c> 의 전체 스키마(ROADMAP Phase 2).
/// 파일시스템이 진실의 원천이다. 이 record 가 디스크 표현이자 API 표현이다.
///
/// S-A 는 <see cref="ScenarioId"/>/<see cref="Name"/>/<see cref="Criteria"/> 만 판정에 사용한다.
/// 나머지 필드(roi·캡처설정·ng_labels)는 스키마로 존재하되 후속 슬라이스(S-D/S-E)에서 소비된다.
/// </summary>
public sealed record Scenario
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>자연어 판정 기준 — VLM 프롬프트에 결합된다.</summary>
    [JsonPropertyName("criteria")] public string Criteria { get; init; } = "";

    [JsonPropertyName("roi")] public Roi Roi { get; init; } = new();

    /// <summary>정지 감지 모션 임계값(0~255 meanAbsDiff). 클라 DEFAULT_MOTION_CONFIG 와 정합.</summary>
    [JsonPropertyName("motion_threshold")] public int MotionThreshold { get; init; } = 6;

    /// <summary>정지로 인정할 연속 프레임 수.</summary>
    [JsonPropertyName("still_frames")] public int StillFrames { get; init; } = 8;

    /// <summary>최소 선명도(라플라시안 분산) — 미달 시 흐림 거부(Phase 3 enforce, 현재 저장만).</summary>
    [JsonPropertyName("min_sharpness")] public int MinSharpness { get; init; } = 100;

    /// <summary>
    /// VLM 전송 이미지의 longest-side 최대 px(다운스케일 레버). <b>0 = 축소 없음(원본)</b>.
    /// query+refs 에 대칭 적용. 측정(M0.1)이 입증한 latency/accuracy 절충 레버 — sweet spot 은 모델/구성
    /// 의존이라 사용자가 시나리오별로 균형점을 찾는다. 보수적 기본 0(비단조 발견 → opt-in).
    /// </summary>
    [JsonPropertyName("max_image_dimension")] public int MaxImageDimension { get; init; }

    /// <summary>
    /// few-shot 기준 이미지 라벨당 최대 장수(비용/latency 레버). <b>0 = zero-shot</b>(refs 미전송).
    /// 기본 4 — 종전 하드코딩 <c>MaxReferencesPerLabel</c> 와 동일(하위호환). 장당 ~2s image-prefill(측정).
    /// </summary>
    [JsonPropertyName("reference_cap")] public int ReferenceCap { get; init; } = 4;

    /// <summary>NG 기준 이미지 레이블: ref_id → 불량 유형명.</summary>
    [JsonPropertyName("ng_labels")] public Dictionary<string, string> NgLabels { get; init; } = new();

    /// <summary>
    /// cloud 오라클(④-B·egress) 허용 여부 — per-scenario opt-in(기본 false=deny). 온프레미스 주권 가드.
    /// 로컬 오라클(openai-compatible)은 egress 아니므로 이 값과 무관. cloud provider 결선 전까지는 가드만 선재.
    /// </summary>
    [JsonPropertyName("allow_cloud_egress")] public bool AllowCloudEgress { get; init; }

    // --- v2 (nullable · additive) — 없으면 v1 에서 파생한다(EffectiveScenario). 비어 있으면 쓰지 않아
    // v1 scenario.json · 응답이 byte 그대로다.

    /// <summary>결과 모양 — 없으면 <c>inspection</c>(OK/NG 검사).</summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>사건 원천 — 없으면 <c>still</c>(정지 촬영).</summary>
    [JsonPropertyName("trigger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Trigger { get; init; }

    /// <summary>판정 값과 위험 등급 — 없으면 OK(확정 가능) · NG(꼭 잡을 값).</summary>
    [JsonPropertyName("classes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ScenarioClass>? Classes { get; init; }

    /// <summary>판정 단 순서 — 없으면 ML 활성일 때 [ml, vlm], 아니면 [vlm].</summary>
    [JsonPropertyName("judges")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ScenarioJudge>? Judges { get; init; }

    /// <summary>조각 키 · 관문 목표 — 없으면 시나리오 하나가 조각, 목표는 기본값(<see cref="ScenarioTargets.Default"/>).</summary>
    [JsonPropertyName("trust")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioTrust? Trust { get; init; }

    /// <summary>
    /// 이 시나리오의 패치 기억이 쓰는 인코더 id — MLoop 에 등록한 인코더(중간 층 출력 · 층 연결 포함). 없으면 설정의 인코더
    /// (<c>UVision:Memory:Encoder</c>). 특징 층의 우열이 대상마다 갈려(기판은 중간 층, 입체 물체는 마지막 층) 시나리오가 고른다.
    /// 패치 점수에만 쓰인다 — 확정 사례 기억(전역 벡터)의 인코더와 거리 문턱은 설치의 것 그대로다.
    /// </summary>
    [JsonPropertyName("patch_encoder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PatchEncoder { get; init; }

    // --- 계수(type: counting)에서만 뜻이 있다. 다른 유형이면 쓰지 않는다.

    /// <summary>
    /// 기대 개수 — 센 수가 <c>expected_count ± count_tolerance</c> 안이면 OK, 밖이면 NG. 계수 시나리오는 이것과
    /// <see cref="ExpectedByClass"/> 중 정확히 하나가 필수.
    /// </summary>
    [JsonPropertyName("expected_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExpectedCount { get; init; }

    /// <summary>허용 오차 — 없으면 0(정확히). <see cref="ExpectedCount"/> 와 함께만 쓴다.</summary>
    [JsonPropertyName("count_tolerance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CountTolerance { get; init; }

    /// <summary>
    /// 종류별 기대 개수(구성 확인) — 기대한 종류가 모두 그 수만큼 있고 더한 것이 없는가를 본다. 종류 하나라도 모자라면 NG.
    /// <see cref="ExpectedCount"/> 와 함께 줄 수 없다(총 개수를 견주는 계수와 종류별로 견주는 구성 확인은 서로 다른 시나리오).
    /// </summary>
    [JsonPropertyName("expected_by_class")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? ExpectedByClass { get; init; }

    /// <summary>
    /// 기대보다 많거나 기대에 없는 종류가 검출됐을 때 — <c>ng</c>(기본, 불량) · <c>warn</c>(항목에 올리되 판정은 그대로) ·
    /// <c>ignore</c>(보지 않는다). <see cref="ExpectedByClass"/> 와 함께만 쓴다.
    /// </summary>
    [JsonPropertyName("extra_policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtraPolicy { get; init; }

    /// <summary>이 신뢰도(0 초과 1 이하) 이상인 상자만 센다 — 없으면 설치 옵션의 기본.</summary>
    [JsonPropertyName("min_confidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinConfidence { get; init; }

    /// <summary>세는 물건의 종류(검출기가 내는 라벨) — 없으면 검출된 모든 종류를 센다. 판정 클래스(<c>classes</c>, OK/NG)와 다르다.</summary>
    [JsonPropertyName("count_classes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CountClasses { get; init; }
}

/// <summary>
/// 시나리오 생성/수정 요청 본문. 생성 시 id 는 <see cref="ScenarioId"/> 가 있으면 그 값, 없으면 <see cref="Name"/> 에서
/// 도출되고, 수정 시 id 는 경로에서 온다(바뀌지 않는다).
/// </summary>
public sealed record ScenarioInput
{
    /// <summary>
    /// 생성할 때 쓸 id(선택) — 데이터 폴더 · 결정 장부 · 판정 사건 · CSV 에 남고 만든 뒤 바뀌지 않는다. 글자는
    /// 영문 · 숫자 · <c>. _ -</c> 만(그 밖 400), 이미 있으면 409. 비우면 이름의 영문 · 숫자에서 만든다 — 한글 이름은
    /// 영문이 남지 않으므로 이 값을 준다. 수정 요청에서는 경로의 id 와 같아야 한다(다르면 400).
    /// </summary>
    [JsonPropertyName("scenario_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScenarioId { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("criteria")] public string Criteria { get; init; } = "";

    [JsonPropertyName("roi")] public Roi Roi { get; init; } = new();

    [JsonPropertyName("motion_threshold")] public int MotionThreshold { get; init; } = 6;

    [JsonPropertyName("still_frames")] public int StillFrames { get; init; } = 8;

    [JsonPropertyName("min_sharpness")] public int MinSharpness { get; init; } = 100;

    [JsonPropertyName("max_image_dimension")] public int MaxImageDimension { get; init; }

    [JsonPropertyName("reference_cap")] public int ReferenceCap { get; init; } = 4;

    [JsonPropertyName("ng_labels")] public Dictionary<string, string> NgLabels { get; init; } = new();

    /// <summary>
    /// cloud 오라클(④-B·egress) 허용 여부 — per-scenario opt-in(기본 false=deny). 온프레미스 주권 가드.
    /// 로컬 오라클(openai-compatible)은 egress 아니므로 이 값과 무관. cloud provider 결선 전까지는 가드만 선재.
    /// </summary>
    [JsonPropertyName("allow_cloud_egress")] public bool AllowCloudEgress { get; init; }

    // --- v2 — 생략(null)하면 수정 시 기존 값을 보존한다(FileScenarioStore.UpdateAsync). 관리 화면이
    // v2 필드를 모르는 동안 다른 필드를 고치다 v2 구성을 지우지 않게.

    [JsonPropertyName("type")] public string? Type { get; init; }

    [JsonPropertyName("trigger")] public string? Trigger { get; init; }

    [JsonPropertyName("classes")] public List<ScenarioClass>? Classes { get; init; }

    [JsonPropertyName("judges")] public List<ScenarioJudge>? Judges { get; init; }

    [JsonPropertyName("trust")] public ScenarioTrust? Trust { get; init; }

    /// <summary>패치 인코더 id — 생략하면 수정 시 기존 값을 보존하고, 빈 문자열은 지운다(설정의 인코더로 돌아간다).</summary>
    [JsonPropertyName("patch_encoder")] public string? PatchEncoder { get; init; }

    // 계수 — 생략하면 수정 시 기존 값을 보존한다(유형이 바뀌면 옮기지 않는다). 계수가 아닌 시나리오에는 줄 수 없다.
    // 기대의 두 모양(총 개수 expected_count · 종류별 expected_by_class)은 서로 바꿔 줄 수 있다 — 한쪽을 주면 다른 쪽(과 그에 묶인
    // count_tolerance · extra_policy)은 보존하지 않고 지운다. 같은 요청에 둘을 함께 주면 400.

    [JsonPropertyName("expected_count")] public int? ExpectedCount { get; init; }

    [JsonPropertyName("count_tolerance")] public int? CountTolerance { get; init; }

    [JsonPropertyName("expected_by_class")] public Dictionary<string, int>? ExpectedByClass { get; init; }

    [JsonPropertyName("extra_policy")] public string? ExtraPolicy { get; init; }

    [JsonPropertyName("min_confidence")] public double? MinConfidence { get; init; }

    [JsonPropertyName("count_classes")] public List<string>? CountClasses { get; init; }

    /// <summary>입력 + 확정 id → 영속 시나리오.</summary>
    public Scenario ToScenario(string scenarioId) => new()
    {
        ScenarioId = scenarioId,
        Name = Name,
        Criteria = Criteria,
        Roi = Roi,
        MotionThreshold = MotionThreshold,
        StillFrames = StillFrames,
        MinSharpness = MinSharpness,
        MaxImageDimension = MaxImageDimension,
        ReferenceCap = ReferenceCap,
        NgLabels = NgLabels,
        AllowCloudEgress = AllowCloudEgress,
        Type = Type,
        Trigger = Trigger,
        Classes = Classes,
        Judges = Judges,
        Trust = Trust,
        PatchEncoder = string.IsNullOrEmpty(PatchEncoder) ? null : PatchEncoder,
        ExpectedCount = ExpectedCount,
        CountTolerance = CountTolerance,
        ExpectedByClass = ExpectedByClass,
        ExtraPolicy = ExtraPolicy,
        MinConfidence = MinConfidence,
        CountClasses = CountClasses,
    };
}
