import { useCallback, useRef, useState, type RefObject } from 'react'

import { inspectImage } from '../lib/api'
import { captureFrame } from '../lib/capture'
import type { InspectResult } from '../lib/types'

export type InspectionPhase = 'idle' | 'capturing' | 'uploading' | 'done' | 'error'

export interface InspectionState {
  phase: InspectionPhase
  latest: InspectResult | null
  history: InspectResult[]
  error: string | null
  /** 캡처→업로드→판정 1회 실행. 진행 중이면 무시(중복 트리거 방지). */
  trigger: () => void
}

const HISTORY_LIMIT = 20

/**
 * 검사 1회의 수명주기 상태머신.
 *
 * C6: 업로드는 즉시 전송(온라인 가정). 오프라인 큐(IndexedDB)는 P3(C 후속)에서
 * 이 경로 앞단에 삽입한다.
 */
export function useInspection(
  videoRef: RefObject<HTMLVideoElement | null>,
  scenarioId: string,
): InspectionState {
  const [phase, setPhase] = useState<InspectionPhase>('idle')
  const [latest, setLatest] = useState<InspectResult | null>(null)
  const [history, setHistory] = useState<InspectResult[]>([])
  const [error, setError] = useState<string | null>(null)
  const inFlight = useRef(false)

  const trigger = useCallback(() => {
    if (inFlight.current) return
    const video = videoRef.current
    if (!video) return
    inFlight.current = true

    void (async () => {
      setError(null)
      try {
        setPhase('capturing')
        const blob = await captureFrame(video)
        setPhase('uploading')
        const result = await inspectImage(blob, scenarioId)
        setLatest(result)
        setHistory((h) => [result, ...h].slice(0, HISTORY_LIMIT))
        setPhase('done')
      } catch (e) {
        setError(e instanceof Error ? e.message : '판정 실패')
        setPhase('error')
      } finally {
        inFlight.current = false
      }
    })()
  }, [videoRef, scenarioId])

  return { phase, latest, history, error, trigger }
}
