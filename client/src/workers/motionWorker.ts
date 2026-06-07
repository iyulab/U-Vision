/// <reference lib="webworker" />
/**
 * 정지 감지 Web Worker.
 *
 * 메인 스레드가 카메라 프레임을 ImageBitmap 으로 전송(transfer)하면,
 * 이 worker 가 OffscreenCanvas 에 다운스케일해 그리고 직전 프레임과 diff 하여
 * 모션 점수를 구한다. 무거운 픽셀 순회를 메인 스레드(UI/카메라)에서 분리한다.
 *
 * 메시지 프로토콜:
 *   ← { type: 'init', config: MotionConfig }
 *   ← { type: 'frame', bitmap: ImageBitmap }   // bitmap 은 transfer
 *   ← { type: 'reset' }
 *   → { type: 'motion', score, isStill, stillStreak, justBecameStill }
 */

import {
  meanAbsDiff,
  StillnessDetector,
  type MotionConfig,
  type StillnessState,
} from '../lib/motion'

type InMessage =
  | { type: 'init'; config: MotionConfig }
  | { type: 'frame'; bitmap: ImageBitmap }
  | { type: 'reset' }

interface OutMessage extends StillnessState {
  type: 'motion'
  score: number
}

let config: MotionConfig | null = null
let detector: StillnessDetector | null = null
let canvas: OffscreenCanvas | null = null
let ctx: OffscreenCanvasRenderingContext2D | null = null
let prev: Uint8ClampedArray | null = null

function ensureCanvas(bitmap: ImageBitmap): OffscreenCanvasRenderingContext2D {
  if (!config) throw new Error('motionWorker: init 전에 frame 수신')
  const w = config.downscaleWidth
  const h = Math.max(1, Math.round((bitmap.height / bitmap.width) * w))
  if (!canvas || canvas.width !== w || canvas.height !== h) {
    canvas = new OffscreenCanvas(w, h)
    ctx = canvas.getContext('2d', { willReadFrequently: true })
    prev = null // 캔버스 크기 바뀌면 이전 프레임 무효
  }
  if (!ctx) throw new Error('motionWorker: 2D 컨텍스트 획득 실패')
  return ctx
}

self.onmessage = (e: MessageEvent<InMessage>) => {
  const msg = e.data

  if (msg.type === 'init') {
    config = msg.config
    detector = new StillnessDetector(config)
    canvas = null
    prev = null
    return
  }

  if (msg.type === 'reset') {
    detector?.reset()
    prev = null
    return
  }

  if (msg.type === 'frame') {
    const bitmap = msg.bitmap
    try {
      if (!config || !detector) return
      const context = ensureCanvas(bitmap)
      context.drawImage(bitmap, 0, 0, canvas!.width, canvas!.height)
      const curr = context.getImageData(0, 0, canvas!.width, canvas!.height).data

      // 첫 프레임은 비교 대상이 없으므로 강제 모션(정지 streak 0 유지).
      const score = prev ? meanAbsDiff(prev, curr) : Number.MAX_SAFE_INTEGER
      prev = curr

      const state = detector.push(score)
      const out: OutMessage = { type: 'motion', score, ...state }
      self.postMessage(out)
    } finally {
      bitmap.close() // transfer 받은 bitmap 자원 해제
    }
  }
}
