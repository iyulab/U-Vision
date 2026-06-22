import { describe, expect, it } from 'vitest'
import { isInspectionFree, shouldTriggerCapture } from './capturePolicy'

describe('isInspectionFree', () => {
  it('idle/done/error/rejected는 free', () => {
    expect(isInspectionFree('idle')).toBe(true)
    expect(isInspectionFree('done')).toBe(true)
    expect(isInspectionFree('error')).toBe(true)
    expect(isInspectionFree('rejected')).toBe(true)
  })
  it('capturing/uploading은 busy', () => {
    expect(isInspectionFree('capturing')).toBe(false)
    expect(isInspectionFree('uploading')).toBe(false)
  })
  it('unavailable은 free (라인 계속, 작업자 물리 보류)', () => {
    expect(isInspectionFree('unavailable')).toBe(true)
  })
})

describe('shouldTriggerCapture', () => {
  const base = { isStill: true, capturedEpisode: false, phase: 'idle' as const, enabled: true }

  it('정지 + 미촬영 + free + enabled → 발화', () => {
    expect(shouldTriggerCapture(base)).toBe(true)
  })
  it('이미 이 에피소드 촬영함 → 발화 안 함(double-fire 방지)', () => {
    expect(shouldTriggerCapture({ ...base, capturedEpisode: true })).toBe(false)
  })
  it('검사기 busy → 발화 안 함(in-flight 드롭 대신 대기)', () => {
    expect(shouldTriggerCapture({ ...base, phase: 'uploading' })).toBe(false)
  })
  it('정지 아님 → 발화 안 함', () => {
    expect(shouldTriggerCapture({ ...base, isStill: false })).toBe(false)
  })
  it('비활성(수동 모드) → 발화 안 함', () => {
    expect(shouldTriggerCapture({ ...base, enabled: false })).toBe(false)
  })
})
