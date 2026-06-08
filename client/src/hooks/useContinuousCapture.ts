import { useEffect, useRef } from 'react'

import { shouldTriggerCapture, type InspectionPhase } from '../lib/capturePolicy'
import type { StillnessState } from '../lib/motion'

/**
 * 연속 자동촬영 코디네이터 — motion 레벨 상태 + 검사 phase 를 조인해 캡처를 발화한다.
 *
 * 무손실 보장: in-flight 중 정지 확정 시 발화하지 않고 capturedEpisode 도 세우지 않는다.
 * 검사기가 free 로 바뀌면 phase 변화로 effect 가 재평가되어, 품목이 여전히 정지 상태면
 * 그때 발화한다(pull-on-free). 모션 재개(!isStill)에서 에피소드 게이트를 재무장한다.
 * 정지 에피소드당 정확히 1회.
 */
export function useContinuousCapture(
  motion: StillnessState | null,
  phase: InspectionPhase,
  trigger: () => void,
  enabled: boolean,
): void {
  const capturedEpisode = useRef(false)

  useEffect(() => {
    const isStill = motion?.isStill ?? false
    if (!isStill) {
      capturedEpisode.current = false // 모션 재개 → 다음 품목 위해 재무장
      return
    }
    if (shouldTriggerCapture({ isStill, capturedEpisode: capturedEpisode.current, phase, enabled })) {
      capturedEpisode.current = true
      trigger()
    }
  }, [motion, phase, enabled, trigger])
}
