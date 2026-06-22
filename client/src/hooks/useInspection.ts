import { useCallback, useRef, useState, type RefObject } from 'react'

import { inspectImage } from '../lib/api'
import { captureFrame } from '../lib/capture'
import { type InspectionPhase } from '../lib/capturePolicy'
import { getDeviceId, getDeviceLabel } from '../lib/deviceIdentity'
import { classifyTriggerError } from '../lib/inspectionError'
import type { Roi } from '../lib/roi'
import type { InspectResult, MlResult } from '../lib/types'

export type { InspectionPhase } from '../lib/capturePolicy'

export interface InspectionState {
  phase: InspectionPhase
  latest: InspectResult | null
  history: InspectResult[]
  error: string | null
  /** 판정 불가(fail-closed) — VLM 검출원 사용 불가(③.5 E2). 정상 시 null. */
  unavailable: { reason: string; mlHint?: MlResult } | null
  /** 캡처→업로드→판정 1회 실행. 진행 중이면 무시(중복 트리거 방지). */
  trigger: () => void
}

const HISTORY_LIMIT = 20

/**
 * 검사 1회의 수명주기 상태머신.
 *
 * 흐림 거부: 캡처 선명도가 minSharpness 미만이면 업로드 없이 거부(VLM 호출 전 차단).
 * 업로드는 즉시 전송(온라인 가정). 오프라인 큐(IndexedDB)는 Phase 3 에서 이 경로 앞단에 삽입한다.
 */
export function useInspection(
  videoRef: RefObject<HTMLVideoElement | null>,
  scenarioId: string,
  roi: Roi,
  minSharpness: number,
): InspectionState {
  const [phase, setPhase] = useState<InspectionPhase>('idle')
  const [latest, setLatest] = useState<InspectResult | null>(null)
  const [history, setHistory] = useState<InspectResult[]>([])
  const [error, setError] = useState<string | null>(null)
  const [unavailable, setUnavailable] = useState<{ reason: string; mlHint?: MlResult } | null>(null)
  const inFlight = useRef(false)

  const trigger = useCallback(() => {
    if (inFlight.current) return
    const video = videoRef.current
    if (!video) return
    inFlight.current = true

    void (async () => {
      setError(null)
      setUnavailable(null)
      try {
        setPhase('capturing')
        const { blob, sharpness } = await captureFrame(video, roi)
        // 흐림 거부: VLM 호출 전 차단(토큰 절약). minSharpness<=0 이면 비활성.
        if (minSharpness > 0 && sharpness < minSharpness) {
          setError(`흐림 — 재시도 (선명도 ${Math.round(sharpness)} < ${minSharpness})`)
          setPhase('rejected')
          return
        }
        setPhase('uploading')
        const result = await inspectImage(blob, scenarioId, getDeviceId(), getDeviceLabel())
        setLatest(result)
        setHistory((h) => [result, ...h].slice(0, HISTORY_LIMIT))
        setPhase('done')
      } catch (e) {
        const c = classifyTriggerError(e)
        setUnavailable(c.unavailable)
        setError(c.error)
        setPhase(c.phase)
      } finally {
        inFlight.current = false
      }
    })()
  }, [videoRef, scenarioId, roi, minSharpness])

  return { phase, latest, history, error, unavailable, trigger }
}
