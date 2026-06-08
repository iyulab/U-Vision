/**
 * 캡처 정책(순수 로직) — "지금 캡처를 발화할까".
 *
 * 핵심: in-flight 중 정지 확정된 품목을 드롭/영구누락하지 않는다(OP-B 1급 결함 수정).
 * detector(레벨 isStill) + 검사기 phase + 에피소드 게이트를 조인. busy면 발화하지 않고,
 * 검사기가 free로 바뀔 때 useContinuousCapture 의 effect 재평가로 발화한다(pull-on-free).
 */
export type InspectionPhase =
  | 'idle'
  | 'capturing'
  | 'uploading'
  | 'done'
  | 'error'
  | 'rejected'

/** 검사기가 새 트리거를 받을 수 있는 상태인가(VLM in-flight 아님). */
export function isInspectionFree(phase: InspectionPhase): boolean {
  return phase === 'idle' || phase === 'done' || phase === 'error' || phase === 'rejected'
}

export interface CaptureDecisionInput {
  /** detector 레벨 신호 — 현재 정지 확정 상태인가. */
  isStill: boolean
  /** 이번 정지 에피소드에서 이미 캡처를 발화했는가(double-fire 방지). */
  capturedEpisode: boolean
  /** 검사 수명주기 단계. */
  phase: InspectionPhase
  /** 자동 모드 활성 여부(수동 모드면 false). */
  enabled: boolean
}

/** 자동 모드에서 지금 캡처를 발화해야 하는가. */
export function shouldTriggerCapture(i: CaptureDecisionInput): boolean {
  return i.enabled && i.isStill && !i.capturedEpisode && isInspectionFree(i.phase)
}
