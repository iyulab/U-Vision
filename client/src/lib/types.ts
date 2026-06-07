/** 서버 `InspectResponse` 미러 — wire 계약(server Models/Inspection.cs 와 동기). */
export type Verdict = 'OK' | 'NG'

export interface InspectResult {
  verdict: Verdict
  findings: string
  confidence: number
  timestamp: string
  image_id: string
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
  ng_labels?: Record<string, string>
}
