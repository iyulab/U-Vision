import { useEffect, useRef, useState, type RefObject } from 'react'

import type { MotionConfig, StillnessState } from '../lib/motion'

const FRAME_INTERVAL_MS = 100 // ~10fps — 정지 감지엔 충분, CPU 절약

type WorkerOut = StillnessState & { type: 'motion'; score: number }

/**
 * 카메라 프레임을 정지감지 worker 로 흘려보내고 모션 상태를 구독한다.
 *
 * 100ms 간격으로 video → ImageBitmap → worker(transfer). worker 가 정지 확정
 * 순간(justBecameStill)을 알리면 onStill 콜백을 1회 호출한다(캡처 트리거).
 */
export function useMotionDetection(
  videoRef: RefObject<HTMLVideoElement | null>,
  config: MotionConfig,
  enabled: boolean,
  onStill: () => void,
): StillnessState | null {
  const [state, setState] = useState<StillnessState | null>(null)
  const onStillRef = useRef(onStill)
  onStillRef.current = onStill

  useEffect(() => {
    if (!enabled) return

    const worker = new Worker(new URL('../workers/motionWorker.ts', import.meta.url), {
      type: 'module',
    })
    worker.postMessage({ type: 'init', config })
    worker.onmessage = (e: MessageEvent<WorkerOut>) => {
      setState(e.data)
      if (e.data.justBecameStill) onStillRef.current()
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
