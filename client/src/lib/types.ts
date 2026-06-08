/** 서버 `InspectResponse` 미러 — wire 계약(server Models/Inspection.cs 와 동기). */
export type Verdict = 'OK' | 'NG'

export interface InspectResult {
  verdict: Verdict
  findings: string
  confidence: number
  timestamp: string
  image_id: string
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
