import { describe, expect, it } from 'vitest'

import { formatDistance, hasMiss, memoryStatusLine } from './memory'
import type { MemoryReplayRow, MemoryReport } from './types'

const base: MemoryReport = { scenario_id: 's', date: '2026-10-23', enabled: true, replayed: 0, replay: [] }

const row = (over: Partial<MemoryReplayRow>): MemoryReplayRow => ({
  max_distance: 0.15,
  configured: true,
  answered: 0,
  teacher_checked: 0,
  teacher_agreement: null,
  labeled: 0,
  label_agreement: null,
  missed: 0,
  miss_rate: null,
  labeled_defects: 0,
  defect_pass_rate: null,
  ...over,
})

describe('memoryStatusLine', () => {
  it('아직 안 지어졌으면 이유를 말한다', () => {
    expect(memoryStatusLine(base)).toContain('아직 지어지지 않았습니다')
  })

  it('지어졌으면 사례 수 · 인코더 · 지난 시간', () => {
    const line = memoryStatusLine(
      { ...base, encoder: 'dinov2-small-0123456789ab', cases: 42, built_at: '2026-10-23T10:00:00Z' },
      new Date('2026-10-23T10:05:00Z'),
    )
    expect(line).toBe('확정 사례 42건 · 인코더 dinov2-small-0123456789ab · 5분 전 갱신')
  })

  it('0건으로 지어진 기억도 지어진 것으로 본다', () => {
    expect(memoryStatusLine({ ...base, encoder: 'mock-1', cases: 0 })).toBe('확정 사례 0건 · 인코더 mock-1')
  })
})

describe('replay rows', () => {
  it('문턱은 소수 두 자리', () => {
    expect(formatDistance(0.1)).toBe('0.10')
  })

  it('놓침이 하나라도 있으면 표시한다', () => {
    expect(hasMiss(row({ missed: 1 }))).toBe(true)
    expect(hasMiss(row({}))).toBe(false)
  })
})
