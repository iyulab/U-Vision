import { act, cleanup, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { useUtcToday, utcToday } from './useUtcToday'

afterEach(() => {
  cleanup()
  vi.useRealTimers()
})

describe('useUtcToday', () => {
  it('UTC 날짜를 쓴다', () => {
    expect(utcToday(new Date('2026-10-22T23:30:00-09:00'))).toBe('2026-10-23')
  })

  it('UTC 자정이 지나면 다음 날로 바뀐다', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-10-22T23:59:00Z'))
    const { result } = renderHook(() => useUtcToday())
    expect(result.current).toBe('2026-10-22')
    act(() => {
      vi.advanceTimersByTime(2 * 60 * 1000)
    })
    expect(result.current).toBe('2026-10-23')
  })
})
