import { afterEach, describe, expect, it } from 'vitest'
import { getCaptureMode, setCaptureMode } from './captureMode'

afterEach(() => localStorage.clear())

describe('captureMode', () => {
  it('기본은 auto', () => {
    expect(getCaptureMode()).toBe('auto')
  })
  it('set/get 라운드트립', () => {
    setCaptureMode('manual')
    expect(getCaptureMode()).toBe('manual')
    setCaptureMode('auto')
    expect(getCaptureMode()).toBe('auto')
  })
  it('알 수 없는 값은 auto로 폴백', () => {
    localStorage.setItem('uvision.captureMode', 'garbage')
    expect(getCaptureMode()).toBe('auto')
  })
})
