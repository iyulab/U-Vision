/**
 * 서버 wire 타입 — 계약(`server/openapi/u-vision.json`)에서 생성한 `contract.gen.ts` 를 앱이 쓰는 이름으로 내보낸다.
 * 필드 설명은 계약 · 서버 모델(`server/src/UVision.Hosting/Models/`)에 있다. 여기 손으로 적는 것은 클라 전용 덧붙임뿐이다.
 * 계약을 바꾸면 `npm run generate:types` (CI 가 다시 생성해 대조한다).
 */
import type * as Wire from './contract.gen'

export type Verdict = Wire.Verdict
export type MlResult = Wire.MlResult
export type ContestedBy = Wire.ContestedBy
export type InspectResultV2 = Wire.InspectResultV2
export type DetectionUnavailable = Wire.DetectionUnavailableResponse
export type StoredResult = Wire.StoredResult
export type Scenario = Wire.Scenario
export type ScenarioClass = Wire.ScenarioClass
export type ScenarioInput = Wire.ScenarioInput
export type Reference = Wire.ReferenceInfo
export type ReferenceLabel = Wire.ReferenceLabel
export type LabelEvent = Wire.LabelEvent
export type LabelAudit = Wire.LabelAudit
export type StoredLabel = Wire.StoredLabel
export type TrainingReadiness = Wire.TrainingReadinessResponse
export type MemoryReport = Wire.MemoryReport
export type AnomalyMap = Wire.AnomalyMap
export type MemoryReplayRow = Wire.MemoryReplayRow
export type MetricsSummary = Wire.MetricsSummary
export type AuthorityTransition = Wire.AuthorityTransition
export type AuthorityState = Wire.AuthorityState
export type AdminStatus = Wire.AdminStatusResponse
export type EventsStatus = Wire.EventsStatusResponse
export type HealthStatus = Wire.HealthResponse
export type Question = Wire.QuestionResponse
export type QuestionAnswer = Wire.QuestionAnswerResponse

/**
 * 시나리오 ROI — 서버 wire 형식(**픽셀** 좌표 `w`/`h`). 계약의 `Roi` 는 요청 본문(`ScenarioInput.roi`)과 같은 스키마라 네 값이
 * 선택으로 적혀 있다(빠지면 서버가 0 으로 읽는다) — 읽는 쪽은 `lib/roi.ts` 가 같은 규칙으로 0 을 채운다.
 * ⚠️ 클라 내부 `lib/roi.ts` 의 `Roi`(0~1 상대 `width`/`height`)와 다르다.
 */
export type ScenarioRoi = Wire.Roi

/**
 * 운영 화면이 쓰는 판정 결과 — 서버 v1 `InspectResponse` 에 클라가 덧붙이는 표시 정보.
 * v2 로 판정했을 때는 v2 응답에서 `contested_by` · `class_label` 을 옮겨 담는다.
 */
export type InspectResult = Wire.InspectResponse & {
  /** 확정에 맞선 아래 단의 must-catch 답(결과 v2 로 판정했을 때만) — 있으면 검토 대기. */
  contested_by?: ContestedBy[]
  /** 시나리오 클래스(결과 v2 로 판정했을 때 — 오투입 확인의 'MATCH'·'MISMATCH'). 서버 v1 응답에는 없다. */
  class_label?: string
  /** 큐에 쌓였다가 늦게 판정된 캡처 — 그 사이 다른 캡처가 화면에 있었다. */
  late?: boolean
  /** 늦게 판정된 캡처의 실제 캡처 시각(`timestamp` 는 서버가 받은 시각). */
  captured_at?: string
}

/** 기준 이미지 종류 — wire 는 숫자(0 = ok, 1 = ng), 화면 · 업로드 · 서빙 경로는 'ok' · 'ng'. */
export const REFERENCE_KIND: Record<ReferenceLabel, 'ok' | 'ng'> = { 0: 'ok', 1: 'ng' }
