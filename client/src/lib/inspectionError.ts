import { DetectionUnavailableError } from './api'
import type { InspectionPhase } from './capturePolicy'
import type { MlResult } from './types'

/** trigger 실행 중 에러를 표시 상태로 분류한 결과(③.5 E2). */
export interface TriggerErrorState {
  phase: InspectionPhase
  error: string | null
  unavailable: { reason: string; mlHint?: MlResult } | null
}

/**
 * trigger 중 발생한 에러를 phase/표시 상태로 분류(③.5 E2) — fail-closed(판정 불가)와
 * transient 오류를 구분한다. useInspection 의 catch 는 이 순수 함수를 호출하는 얇은 배선이다.
 */
export function classifyTriggerError(e: unknown): TriggerErrorState {
  if (e instanceof DetectionUnavailableError)
    return {
      phase: 'unavailable',
      error: null,
      unavailable: { reason: e.reason, mlHint: e.mlHint },
    }
  return {
    phase: 'error',
    error: e instanceof Error ? e.message : '판정 실패',
    unavailable: null,
  }
}
