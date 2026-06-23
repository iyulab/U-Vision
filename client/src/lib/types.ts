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
  /** 클래스 식별자(현재 'OK'|'NG'). string — 다중분류 대비 개방형. */
  label: string
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

/** 권한 단계 상태(A1) — 서버 `AuthorityState` 미러. */
export interface AuthorityState {
  stage: import('./authority').AuthorityStage
  previous_stage?: import('./authority').AuthorityStage | null
  updated_at: string
  updated_by: string
  reason?: string | null
  history: AuthorityTransition[]
}
