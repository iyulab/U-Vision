/** 서버 `InspectResponse` 미러 — wire 계약(server Models/Inspection.cs 와 동기). */
export type Verdict = 'OK' | 'NG'

/**
 * 전용 ML 교차검증 결과(③ 2중체크) — 서버 `MlResult` 미러.
 * class-agnostic 라벨(예: 'ok'|'ng') + 신뢰도. ML 비활성(기본 none) 시 부모에서 생략된다.
 */
export interface MlResult {
  label: string
  confidence: number
}

export interface InspectResult {
  verdict: Verdict
  findings: string
  confidence: number
  timestamp: string
  image_id: string
  /** ③ 2중체크: 전용 ML 교차검증 결과. ML 비활성/실패 시 부재(additive·optional). */
  ml?: MlResult
  /** VLM·ML 판정 일치 여부. ML 없으면 부재. */
  agreement?: boolean
  /** 불일치/저신뢰 → 사람·오라클 검토 필요(③→④). ML 없으면 부재. */
  requires_review?: boolean
  /** 운영 자세(A1) — 차단형 확인 게이트일 때만 'review_block'. 그 외 부재. */
  posture?: 'review_block'
  /** 시나리오 클래스(결과 v2 로 판정했을 때 — 오투입 확인의 'MATCH'·'MISMATCH'). 클라 표시용, 서버 v1 응답에는 없다. */
  class_label?: string
  /** 큐에 쌓였다가 늦게 판정된 캡처(클라 표시용) — 그 사이 다른 캡처가 화면에 있었다. */
  late?: boolean
  /** 늦게 판정된 캡처의 실제 캡처 시각(클라 표시용 — `timestamp` 는 서버가 받은 시각). */
  captured_at?: string
}

/** 서버 `InspectResultV2`(`POST /v2/inspect`) 미러 — 판정 불가(503)는 DetectionUnavailableError 로 온다. */
export interface InspectResultV2 {
  decision_id: string
  scenario_id: string
  timestamp: string
  class?: string
  settled_by?: { tier: string; kind: string }
  confidence?: number
  findings?: string
  requires_review?: boolean
  path?: { tier: string; outcome: 'settled' | 'shadowed' | 'escalated' | 'abstained' }[]
  detection_unavailable?: boolean
  reason?: string
  device_id?: string
  device_label?: string
  /** 장치 캡처 시각(UTC) — 요청이 보냈을 때만. `timestamp` 는 서버가 받은 시각. */
  captured_at?: string
}

/**
 * 서버 `DetectionUnavailableResponse`(503) 미러 — 주 검출원(VLM) 사용 불가 시 본문(③.5 E2).
 * 200 성공 응답과 별개 — fail-closed 운영 자세를 클라가 '판정 불가'로 표면화한다.
 */
export interface DetectionUnavailable {
  detection_unavailable: true
  reason: string
  /** ML 참고 의견(VLM-down·ML-up 시). verdict 아님. */
  ml_hint?: MlResult
}

/** 서버 `StoredResult`(영속 레코드, `{image_id}.json`) 미러 — 결과 조회용. */
export interface StoredResult {
  scenario_id: string
  image_id: string
  verdict: Verdict
  findings: string
  confidence: number
  timestamp: string
  /** 짝 이미지 파일명(예: `img_{guid}.jpg`). */
  image_file: string
  /** 촬영 태블릿 안정 식별자. 구 레코드/누락 시 "". */
  device_id: string
  /** 태블릿 표시 라벨(예: "라인 A 입구"). 구 레코드/누락 시 "". */
  device_label: string
  /** ③ 2중체크: 전용 ML 교차검증 결과. ML 비활성 레코드엔 부재(additive·optional). */
  ml?: MlResult
  /** VLM·ML 판정 일치 여부. ML 없으면 부재. */
  agreement?: boolean
  /** 불일치/저신뢰 → 검토 필요(③→④). ML 없으면 부재. */
  requires_review?: boolean
  /** 오투입 확인의 기대 품번 · 장치가 읽은 코드(요청이 보냈을 때만). */
  expected_part?: string
  observed_code?: string
}

/**
 * 시나리오 ROI — 서버 wire 형식(**픽셀** 좌표 `w`/`h`).
 * ⚠️ 클라 내부 `lib/roi.ts` 의 `Roi`(0~1 상대 `width`/`height`)와 다르다.
 * S-C 는 이 필드를 그대로 통과시키고, 픽셀↔상대 변환은 S-E(ROI 편집기)가 소유한다.
 */
export interface ScenarioRoi {
  x: number
  y: number
  w: number
  h: number
}

/** 서버 `Scenario`(scenario.json) 미러 — 전체 스키마. */
export interface Scenario {
  scenario_id: string
  name: string
  criteria: string
  roi: ScenarioRoi
  motion_threshold: number
  still_frames: number
  min_sharpness: number
  /** VLM 전송 이미지 longest-side 최대 px(다운스케일 레버). 0 = 축소 없음(원본). */
  max_image_dimension: number
  /** few-shot 기준 이미지 라벨당 최대 장수(레버). 0 = zero-shot. */
  reference_cap: number
  ng_labels: Record<string, string>
  /** 클라우드 egress 허용 여부(④-B 오라클 게이트). 부재=false로 취급. */
  allow_cloud_egress?: boolean
  /** 시나리오 유형(v2) — 부재 = 'inspection'(OK/NG 검사). 'verification' 은 기대 품번과 대조하는 오투입 확인. */
  type?: 'inspection' | 'verification'
}

/** 기준 이미지 메타데이터 — 서버 `ReferenceInfo` 미러. */
export interface Reference {
  ref_id: string
  label: 'ok' | 'ng'
  ng_label?: string | null
}

/** 시나리오 생성/수정 요청 본문 — 서버 `ScenarioInput` 미러(scenario_id 제외). */
export interface ScenarioInput {
  name: string
  criteria?: string
  roi?: ScenarioRoi
  motion_threshold?: number
  still_frames?: number
  min_sharpness?: number
  max_image_dimension?: number
  reference_cap?: number
  ng_labels?: Record<string, string>
  /** 시나리오 유형(v2) — 생략하면 서버가 저장된 값을 유지한다. */
  type?: 'inspection' | 'verification'
}

/** 라벨 이력 이벤트(C1 provenance) — 서버 `LabelEvent` 미러. */
export interface LabelEvent {
  label: string
  /** 라벨러 = device UUID(cycle-35). 구 이벤트 시 ''. */
  by: string
  at: string
  mode: 'label' | 'audit' | 'oracle'
}

/** 감사 상태(C1) — 서버 `LabelAudit` 미러. */
export interface LabelAudit {
  status: 'unaudited' | 'consistent' | 'conflicted' | 'resolved'
  at?: string
}

/** 서버 `StoredLabel`(사이드카 `{image_id}.label.json`) 미러. */
export interface StoredLabel {
  image_id: string
  /** operative(사람) 라벨. oracle-only/미라벨 사이드카엔 부재. */
  label?: string
  timestamp: string
  /** append-only 이력(C1). 구 사이드카엔 부재(서버가 합성). */
  history?: LabelEvent[]
  /** 감사 상태(C1). 없으면 unaudited. */
  audit?: LabelAudit
}

/**
 * 서버 `MetricsSummary`(B3 집계, `GET /api/metrics`) 미러 — 시나리오·날짜 관측 신호.
 * 원시 카운트 + 파생 비율(분모 0 → null = 데이터 없음, 0% 위장 아님). NG recall 은 사람 라벨 조인.
 */
export interface MetricsSummary {
  scenario_id: string
  date: string
  inspections: number
  ml_degraded: number
  agreements: number
  reviews_required: number
  labeled: number
  labeled_ng: number
  vlm_ng_hits: number
  ml_ng_scored: number
  ml_ng_hits: number
  /** 일치율 = agreements / 비-degrade. 비-degrade 0 건이면 null. */
  agreement_rate: number | null
  /** 검토율 = reviews_required / 비-degrade. */
  review_rate: number | null
  /** degrade율 = ml_degraded / inspections. */
  degrade_rate: number | null
  /** VLM NG recall = vlm_ng_hits / labeled_ng. 라벨된 NG 0 건이면 null. */
  vlm_ng_recall: number | null
  /** ML NG recall = ml_ng_hits / ml_ng_scored. */
  ml_ng_recall: number | null
  /** 주 검출원(VLM) 사용 불가로 자동 판정 못 한 건(fail-closed, ③.5 E2). */
  fail_closed: number
  /** fail-closed율 = fail_closed / (inspections + fail_closed). 총 시도 0 이면 null. */
  fail_closed_rate: number | null
  /** 블라인드 감사된 라벨 수(C1). */
  audited: number
  /** 감사 일관 라벨 수. */
  label_consistent: number
  /** 미해소 충돌 라벨 수(검토 큐). */
  label_conflicts_open: number
  /** 라벨 일관성률 = label_consistent / audited. 감사 0 건이면 null. */
  label_consistency_rate: number | null
  /** ④-B 오라클↔사람 비교 대상 건(operative+oracle 소견 모두 존재). */
  oracle_evaluated: number
  /** 오라클 ≠ 사람 operative 불일치 건 — 라벨러 계통편향 신호(flag-only·기록만). */
  oracle_conflicts: number
  /** requires_review인데 오라클 미처리 건 — 스윕 백로그 깊이. */
  oracle_pending: number
  /** 오라클↔사람 일관성률 = (oracle_evaluated−oracle_conflicts)/oracle_evaluated. 비교 0건이면 null. */
  oracle_consistency_rate: number | null
  /** 오라클 커버리지율 = (reviews_required−oracle_pending)/reviews_required. 검토 0건이면 null. */
  oracle_coverage_rate: number | null
  /** 격상 자격 신호(A1). 데이터 불충분/조건 미달이면 부재 또는 false. */
  promotion_eligible?: boolean
}

/** 단계 전이 이력(A1 provenance) — 서버 `AuthorityTransition` 미러. */
export interface AuthorityTransition {
  from: string
  to: string
  at: string
  by: string
  mode: string
  reason?: string | null
}

/** 관리자 PIN 설정 상태(최초 설정 부트스트랩) — 서버 `AdminStatusResponse` 미러. */
export interface AdminStatus {
  /** PIN 이 어떤 경로로든(config 또는 파일) 설정됨. */
  pin_configured: boolean
  /** 부트스트랩 활성 && 미설정 — 콘솔이 최초 설정 화면을 띄워야 함. */
  setup_required: boolean
}

/** 권한 단계 상태(A1) — 서버 `AuthorityState` 미러. */
export interface AuthorityState {
  stage: import('./authority').AuthorityStage
  previous_stage?: import('./authority').AuthorityStage | null
  updated_at: string
  updated_by: string
  reason?: string | null
  history: AuthorityTransition[]
}
