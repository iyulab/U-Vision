/**
 * 정지 감지 순수 로직 — worker/DOM 비의존이라 단위 테스트 가능.
 *
 * 전략(리서치 근거): 프레임을 작게 다운스케일한 뒤 픽셀 평균 절대차로 모션 점수를
 * 구하고, 연속 N프레임이 임계 미만이면 "정지"로 판정한다. 다운스케일은 640×480
 * (≈30만 픽셀) 순회 비용을 줄이는 핵심 최적화.
 */

export interface MotionConfig {
  /** 모션 점수 임계 (0~255, 평균 채널 차이). 미만이면 정지 프레임. */
  motionThreshold: number
  /** 정지 확정까지 필요한 연속 정지 프레임 수. */
  stillFrames: number
  /** 다운스케일 폭(px). 높이는 종횡비로 자동. 작을수록 빠름·둔감. */
  downscaleWidth: number
}

export const DEFAULT_MOTION_CONFIG: MotionConfig = {
  motionThreshold: 6,
  stillFrames: 8,
  downscaleWidth: 64,
}

/**
 * 두 RGBA 버퍼의 픽셀당 평균 절대차(알파 제외, RGB 3채널 평균).
 * 길이가 다르면 짧은 쪽 기준(방어적). 반환 0~255.
 */
export function meanAbsDiff(a: Uint8ClampedArray, b: Uint8ClampedArray): number {
  const len = Math.min(a.length, b.length)
  if (len === 0) return 0
  let sum = 0
  let pixels = 0
  for (let i = 0; i + 2 < len; i += 4) {
    const d = Math.abs(a[i] - b[i]) + Math.abs(a[i + 1] - b[i + 1]) + Math.abs(a[i + 2] - b[i + 2])
    sum += d / 3
    pixels++
  }
  return pixels ? sum / pixels : 0
}

export interface StillnessState {
  /** 정지 확정 여부(streak >= stillFrames). 레벨 신호 — 정지 유지 중 계속 true. */
  isStill: boolean
  /** 현재 연속 정지 프레임 수. */
  stillStreak: number
}

/**
 * 모션 점수 스트림을 받아 정지 상태를 추적하는 상태머신(순수 레벨 신호).
 *
 * edge(justBecameStill) 책임은 제거됐다 — "지금 캡처할까"는 검사기 free 상태와
 * 조인해야 하는 정책이므로 capturePolicy/useContinuousCapture 가 소유한다.
 * 이 클래스는 "정지인가?"만 답한다.
 */
export class StillnessDetector {
  private streak = 0

  constructor(private readonly config: MotionConfig) {}

  push(score: number): StillnessState {
    if (score < this.config.motionThreshold) {
      this.streak++
    } else {
      this.streak = 0
    }
    return { isStill: this.streak >= this.config.stillFrames, stillStreak: this.streak }
  }

  reset(): void {
    this.streak = 0
  }
}
