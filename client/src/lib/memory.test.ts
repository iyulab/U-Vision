import { describe, expect, it } from 'vitest'

import { canApplyRecommendation, distanceUpdateInput, formatDistance, hasMiss, memoryStatusLine, MEMORY_WINDOWS, recommendationView } from './memory'
import type { MemoryRecommendation, MemoryReplayRow, MemoryReport, Scenario } from './types'

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

const rec = (over: Partial<MemoryRecommendation>): MemoryRecommendation => ({
  max_distance: 0.1,
  answered: 12,
  labeled_defects: 149,
  missed: 0,
  defect_pass_upper_95: 0.0199,
  sufficient: true,
  target_miss_rate: 0.02,
  defects_needed: 149,
  reason: '문턱 0.1 까지 불량 149장 중 OK 로 지나간 것이 없다.',
  ...over,
})

describe('recommendationView', () => {
  it('근거가 충분하면 «추천»', () => {
    const v = recommendationView(rec({}))
    expect(v.tone).toBe('sufficient')
    expect(v.headline).toBe('추천 문턱 0.10')
    expect(v.detail).toContain('불량 149장 중 통과 0')
  })

  it('증거가 모자라면 «후보» 이고 필요한 불량 라벨 수를 말한다', () => {
    const v = recommendationView(rec({ sufficient: false, labeled_defects: 6, defect_pass_upper_95: 0.393 }))
    expect(v.tone).toBe('candidate')
    expect(v.headline).toBe('후보 문턱 0.10')
    expect(v.detail).toContain('불량 6장 중 통과 0')
    expect(v.detail).toContain('통과율 95% 상한 39%')
    expect(v.detail).toContain('목표 2%')
    expect(v.detail).toContain('149장')
  })

  it('문턱이 없으면 이유만 말한다', () => {
    const v = recommendationView(
      rec({ max_distance: undefined, defect_pass_upper_95: undefined, sufficient: false, labeled_defects: 0, reason: '사람이 확정한 불량 라벨이 없어 놓침을 잴 수 없다.' }),
    )
    expect(v.tone).toBe('none')
    expect(v.headline).toBe('추천할 근거가 아직 없습니다')
    expect(v.detail).toBe('사람이 확정한 불량 라벨이 없어 놓침을 잴 수 없다.')
  })
})

describe('canApplyRecommendation', () => {
  it('추천 문턱이 있고 지금 설정값과 다를 때만', () => {
    expect(canApplyRecommendation(rec({ max_distance: 0.1 }), 0.15)).toBe(true)
    expect(canApplyRecommendation(rec({ max_distance: 0.15 }), 0.15)).toBe(false)
    expect(canApplyRecommendation(rec({ max_distance: undefined }), 0.15)).toBe(false)
    expect(canApplyRecommendation(rec({ max_distance: 0.1 }), undefined)).toBe(true)
  })
})

describe('distanceUpdateInput', () => {
  const scenario: Scenario = {
    scenario_id: 's',
    name: '데모',
    criteria: '기준',
    roi: { x: 1, y: 2, w: 3, h: 4 },
    motion_threshold: 7,
    still_frames: 9,
    min_sharpness: 120,
    max_image_dimension: 512,
    reference_cap: 3,
    ng_labels: { ref1: '스크래치' },
    allow_cloud_egress: true,
    type: 'inspection',
    memory_max_distance: 0.15,
  }

  it('기존 v1 필드를 그대로 싣고 문턱만 바꾼다 — PUT 이 다른 설정을 되돌리지 않게', () => {
    const input = distanceUpdateInput(scenario, 0.08)
    expect(input).toEqual({
      name: '데모',
      criteria: '기준',
      roi: { x: 1, y: 2, w: 3, h: 4 },
      motion_threshold: 7,
      still_frames: 9,
      min_sharpness: 120,
      max_image_dimension: 512,
      reference_cap: 3,
      memory_max_distance: 0.08,
    })
  })

  it('유형 · 불량 유형 · 계수 필드 · 클라우드 송출 허용은 싣지 않는다(서버가 보존한다)', () => {
    const input = distanceUpdateInput(scenario, 0.08)
    for (const key of ['type', 'classes', 'judges', 'trust', 'expected_count', 'ng_labels', 'allow_cloud_egress']) expect(input).not.toHaveProperty(key)
  })
})

describe('MEMORY_WINDOWS', () => {
  it('하루가 기본이고 서버 상한(90일) 안이다', () => {
    expect(MEMORY_WINDOWS[0].days).toBe(1)
    expect(Math.max(...MEMORY_WINDOWS.map((w) => w.days))).toBeLessThanOrEqual(90)
  })
})
