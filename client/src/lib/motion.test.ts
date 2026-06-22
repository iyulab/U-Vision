import { describe, expect, it } from 'vitest'
import {
  DEFAULT_MOTION_CONFIG,
  meanAbsDiff,
  StillnessDetector,
  type MotionConfig,
} from './motion'

function buf(values: number[]): Uint8ClampedArray {
  return new Uint8ClampedArray(values)
}

describe('meanAbsDiff', () => {
  it('동일 버퍼는 0', () => {
    const a = buf([10, 20, 30, 255, 40, 50, 60, 255])
    expect(meanAbsDiff(a, a)).toBe(0)
  })

  it('알파 채널은 무시한다', () => {
    const a = buf([0, 0, 0, 255])
    const b = buf([0, 0, 0, 0]) // 알파만 다름
    expect(meanAbsDiff(a, b)).toBe(0)
  })

  it('RGB 평균 차이를 반환한다', () => {
    const a = buf([0, 0, 0, 255])
    const b = buf([30, 30, 30, 255]) // 각 채널 30 차이 → 평균 30
    expect(meanAbsDiff(a, b)).toBe(30)
  })

  it('빈 버퍼는 0', () => {
    expect(meanAbsDiff(buf([]), buf([]))).toBe(0)
  })
})

describe('StillnessDetector', () => {
  const config: MotionConfig = { motionThreshold: 6, stillFrames: 3, downscaleWidth: 64 }

  it('연속 정지 프레임이 임계에 도달하면 isStill', () => {
    const d = new StillnessDetector(config)
    expect(d.push(2).isStill).toBe(false) // streak 1
    expect(d.push(2).isStill).toBe(false) // streak 2
    expect(d.push(2).isStill).toBe(true) // streak 3
  })

  it('정지 유지 중 isStill은 레벨로 계속 true', () => {
    const d = new StillnessDetector(config)
    d.push(1)
    d.push(1)
    expect(d.push(1).isStill).toBe(true)
    expect(d.push(1).isStill).toBe(true) // 레벨 — 계속 정지면 계속 true
  })

  it('모션이 임계 이상이면 streak·isStill 리셋', () => {
    const d = new StillnessDetector(config)
    d.push(1)
    d.push(1)
    d.push(1)
    expect(d.push(100).stillStreak).toBe(0)
    expect(d.push(100).isStill).toBe(false)
  })

  it('모션 재개 후 다시 정지하면 isStill 재진입', () => {
    const d = new StillnessDetector(config)
    d.push(1)
    d.push(1)
    expect(d.push(1).isStill).toBe(true) // 1차 정지
    d.push(100) // 모션 재개
    d.push(1)
    d.push(1)
    expect(d.push(1).isStill).toBe(true) // 2차 정지
  })

  it('reset 후 streak 초기화', () => {
    const d = new StillnessDetector(config)
    d.push(1)
    d.reset()
    expect(d.push(1).stillStreak).toBe(1)
  })
})

describe('DEFAULT_MOTION_CONFIG', () => {
  it('합리적 기본값', () => {
    expect(DEFAULT_MOTION_CONFIG.downscaleWidth).toBeGreaterThan(0)
    expect(DEFAULT_MOTION_CONFIG.stillFrames).toBeGreaterThan(0)
  })
})
