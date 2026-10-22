import { describe, expect, it } from 'vitest'

import { readinessLine } from './training'
import type { TrainingReadiness } from './types'

const base: TrainingReadiness = {
  scenario_id: 's',
  classes: [
    { id: 'OK', count: 40 },
    { id: 'NG', count: 12 },
  ],
  min_per_class: 30,
  rebuild_min_new_labels: 30,
}

describe('readinessLine', () => {
  it('모델 없음 — 기준 미달이면 라벨이 더 필요, 닿으면 빌드 가능', () => {
    expect(readinessLine({ ...base, build_ready: false })).toEqual({
      tone: 'pending',
      text: '전용 모델 빌드까지 사람 확인 라벨이 더 필요합니다 — OK 40/30 · NG 12/30',
    })
    expect(readinessLine({ ...base, build_ready: true }).tone).toBe('ready')
  })

  it('모델 있음 — 새 라벨 수와 재빌드 제안', () => {
    expect(readinessLine({ ...base, active_version: 'v2', new_labels_since_model: 31, rebuild_suggested: true })).toEqual({
      tone: 'ready',
      text: '모델 v2 이후 새 라벨 31건 — 재빌드를 제안합니다(기준 30건)',
    })
    expect(readinessLine({ ...base, active_version: 'v2', new_labels_since_model: 3, rebuild_suggested: false }).tone).toBe('pending')
  })

  it('모델이 배운 데이터셋을 모르면 세지 않는다', () => {
    expect(readinessLine({ ...base, active_version: 'v1' }).text).toContain('셀 수 없습니다')
  })
})
