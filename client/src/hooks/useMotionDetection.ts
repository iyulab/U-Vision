import { useEffect, useState, type RefObject } from 'react'

import type { MotionConfig, StillnessState } from '../lib/motion'

const FRAME_INTERVAL_MS = 100 // ~10fps — 정지 감지엔 충분, CPU 절약

type WorkerOut = StillnessState & { type: 'motion'; score: number }

/**
 * 카메라 프레임을 정지감지 worker 로 흘려보내고 모션 상태(레벨)를 구독한다.
 *
 * 100ms 간격으로 video → ImageBitmap → worker(transfer). worker 가 레벨 상태
 * (isStill/stillStreak)를 알리면 구독자가 반환값으로 읽는다. "지금 캡처할까"는
 * useContinuousCapture 가 검사기 free 상태와 조인해 결정한다(여기서는 결정하지 않음).
 */
export function useMotionDetection(
  videoRef: RefObject<HTMLVideoElement | null>,
  config: MotionConfig,
  enabled: boolean,
): StillnessState | null {
  const [state, setState] = useState<StillnessState | null>(null)

  useEffect(() => {
    // 비활성(수동 모드 등) — 마지막 모션 상태를 비워, 재활성 시 stale isStill 로 인한
    // spurious 발화(useContinuousCapture)가 없도록 한다(무손실 경로 보호).
    if (!enabled) {
      setState(null)
      return
    }

    const worker = new Worker(new URL('../workers/motionWorker.ts', import.meta.url), {
      type: 'module',
    })
    worker.postMessage({ type: 'init', config })
    worker.onmessage = (e: MessageEvent<WorkerOut>) => {
      setState(e.data)
    }

    let busy = false
    async function tick() {
      const video = videoRef.current
      if (!video || video.readyState < 2 || busy) return
      busy = true
      try {
        const bitmap = await createImageBitmap(video)
        worker.postMessage({ type: 'frame', bitmap }, [bitmap])
      } catch {
        // 프레임 스킵(다음 tick 에서 복구)
      } finally {
        busy = false
      }
    }

    const timer = window.setInterval(() => void tick(), FRAME_INTERVAL_MS)
    return () => {
      window.clearInterval(timer)
      worker.terminate()
    }
  }, [enabled, config, videoRef])

  return state
}
