import { describe, expect, it } from 'vitest'

import { streamSummary } from './streamScenario'
import type { Scenario } from './types'

describe('streamSummary', () => {
  it('소스와 규칙을 사람이 읽는 한 줄씩으로 — 카메라는 별칭만', () => {
    const s = {
      scenario_id: 'watch',
      sources: [{ id: 'cam1', camera_ref: 'dock-1', zones: [{ id: 'shelf', polygon: [] }], lines: [] }],
      rules: [
        { id: 'stock', kind: 'count', source: 'cam1', zone: 'shelf', class: 'box' },
        { id: 'gate', kind: 'alarm', source: 'cam1', line: 'door', class: 'person' },
      ],
    } as unknown as Scenario

    const out = streamSummary(s)

    expect(out.sources).toEqual(['cam1 · 카메라 dock-1 · 구역 1개 · 선 0개'])
    expect(out.rules).toEqual(['stock · 개수 · box · 소스 cam1 · 구역 shelf', 'gate · 경보 · person · 소스 cam1 · 선 door'])
  })

  it('정의가 비어 있어도 던지지 않는다', () => {
    expect(streamSummary({ scenario_id: 'w' } as unknown as Scenario)).toEqual({ sources: [], rules: [] })
  })
})
