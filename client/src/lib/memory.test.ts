import { describe, expect, it } from 'vitest'

import { canApplyRecommendation, distanceUpdateInput, formatDistance, hasMiss, memoryStatusLine, MEMORY_WINDOWS, neighborCurveView, recommendationView } from './memory'
import type { MemoryNeighborCurve, MemoryRecommendation, MemoryReplayRow, MemoryReport, Scenario } from './types'

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

describe('neighborCurveView', () => {
  const curve = (rows: MemoryNeighborCurve['rows']): MemoryNeighborCurve => ({ max_distance: 0.15, rows })
  const r = (over: Partial<MemoryNeighborCurve['rows'][number]>): MemoryNeighborCurve['rows'][number] => ({
    k: 5, configured: false, counted: 40, answered: 30, answer_rate: 0.75, labeled_defects: 10, missed: 0, defect_pass_rate: 0, ...over,
  })

  it('k 마다 응답률과 불량 통과를 나란히 보인다 — 설정된 k 는 표지', () => {
    const v = neighborCurveView(curve([r({ k: 1, answered: 38, answer_rate: 0.95, missed: 4, defect_pass_rate: 0.4 }), r({ k: 5, configured: true })]), 149)
    expect(v.rows.map((x) => x.k)).toEqual([1, 5])
    expect(v.rows[0]).toMatchObject({ answer: '95% (38/40)', defectPass: '4/10 (40%)', hasMiss: true, configured: false })
    expect(v.rows[1]).toMatchObject({ answer: '75% (30/40)', defectPass: '0/10 (0%)', hasMiss: false, configured: true })
  })

  it('불량 라벨이 없는 행은 통과를 «측정 불가» 로 말한다 — 0 으로 꾸미지 않는다', () => {
    const v = neighborCurveView(curve([r({ labeled_defects: 0, defect_pass_rate: undefined })]), 149)
    expect(v.rows[0].defectPass).toBe('불량 라벨 없음 — 측정 불가')
  })

  it('불량 라벨이 목표를 주장할 만큼 안 되면 경고하고, 충분하면 경고가 없다', () => {
    const thin = neighborCurveView(curve([r({ labeled_defects: 12 }), r({ k: 9, labeled_defects: 12 })]), 149)
    expect(thin.thin).toBe(true)
    expect(thin.warning).toContain('불량 라벨이 12장뿐')
    expect(thin.warning).toContain('149장')
    const enough = neighborCurveView(curve([r({ labeled_defects: 200 })]), 149)
    expect(enough.thin).toBe(false)
    expect(enough.warning).toBeNull()
  })

  it('설명은 서버가 k 를 고르지 않는다고 밝히고 문턱을 말한다', () => {
    const v = neighborCurveView(curve([r({})]), 149)
    expect(v.caption).toContain('거리 문턱 0.15')
    expect(v.caption).toContain('서버는 k 를 고르지 않습니다')
  })
})
