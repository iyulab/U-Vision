namespace UVision.Api.Models;

/// <summary>
/// 관심 영역 — **상대 좌표(0~1)**. 캡처/감지가 집중할 사각 구역.
/// 해상도 독립(태블릿마다 카메라 해상도가 다르므로): 클라가 같은 ROI 표현을 쓰고
/// 변환은 필드명 매핑(w↔width)뿐. 0 폭/높이는 "전체 프레임"을 뜻한다(미설정 기본).
/// (cycle-21 RE-PLAN: 픽셀 → 상대좌표 — ROI 가 미사용이던 첫 소비 시점이라 마이그레이션 없음)
/// </summary>
public sealed record Roi
{
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
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
    public required string ScenarioId { get; init; }

    public required string Name { get; init; }

    /// <summary>자연어 판정 기준 — VLM 프롬프트에 결합된다.</summary>
    public string Criteria { get; init; } = "";

    public Roi Roi { get; init; } = new();

    /// <summary>정지 감지 모션 임계값(0~255 meanAbsDiff). 클라 DEFAULT_MOTION_CONFIG 와 정합.</summary>
    public int MotionThreshold { get; init; } = 6;

    /// <summary>정지로 인정할 연속 프레임 수.</summary>
    public int StillFrames { get; init; } = 8;

    /// <summary>최소 선명도(라플라시안 분산) — 미달 시 흐림 거부(Phase 3 enforce, 현재 저장만).</summary>
    public int MinSharpness { get; init; } = 100;

    /// <summary>NG 기준 이미지 레이블: ref_id → 불량 유형명.</summary>
    public Dictionary<string, string> NgLabels { get; init; } = new();
}

/// <summary>
/// 시나리오 생성/수정 요청 본문 — <see cref="Scenario"/> 에서 서버가 결정하는 <c>scenario_id</c> 를 뺀 것.
/// 생성 시 id 는 <see cref="Name"/> 에서 도출되고, 수정 시 id 는 경로에서 온다.
/// </summary>
public sealed record ScenarioInput
{
    public required string Name { get; init; }

    public string Criteria { get; init; } = "";

    public Roi Roi { get; init; } = new();

    public int MotionThreshold { get; init; } = 6;

    public int StillFrames { get; init; } = 8;

    public int MinSharpness { get; init; } = 100;

    public Dictionary<string, string> NgLabels { get; init; } = new();

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
        NgLabels = NgLabels,
    };
}
