import { describe, expect, it } from 'vitest'

import type { DecisionEvidence } from './types'
import { outcomeText, kindText, evidenceSummary, notableAnnotations, tierText, agreementText, opinionText, votesText } from './evidence'

const BASE: DecisionEvidence = {
  image_id: 'img_1',
  scenario_id: 'demo',
  date: '2026-11-04',
  class: 'OK',
  confidence: 0.93,
  ledger_recorded: true,
}

describe('outcomeText', () => {
  it('단 결과를 사람이 읽는 말로 — 모르는 값은 그대로', () => {
    expect(outcomeText('settled')).toBe('확정')
    expect(outcomeText('shadowed')).toBe('답했으나 확정 권한 없음')
    expect(outcomeText('escalated')).toBe('놓치면 안 되는 값이라 위로')
    expect(outcomeText('abstained')).toBe('기권')
    expect(outcomeText('future-outcome')).toBe('future-outcome')
  })
})

describe('kindText', () => {
  it('판정자 종류', () => {
    expect(kindText('vlm')).toBe('VLM')
    expect(kindText('ml')).toBe('전용 ML')
    expect(kindText('system1')).toBe('system 1(기억·대조)')
    expect(kindText('human')).toBe('사람')
    expect(kindText('x')).toBe('x')
  })
})

describe('evidenceSummary', () => {
  it('장부가 있으면 확정한 단을 한 줄로', () => {
    expect(
      evidenceSummary({ ...BASE, settled_by: { tier: 'vlm', kind: 'vlm' } }),
    ).toBe('VLM(vlm 단)이 확정')
  })

  it('경로가 여러 단이면 아래 단이 어떻게 됐는지 덧붙인다', () => {
    expect(
      evidenceSummary({
        ...BASE,
        settled_by: { tier: 'vlm', kind: 'vlm' },
        path: [
          { tier: 'ml', kind: 'ml', outcome: 'shadowed' },
          { tier: 'vlm', kind: 'vlm', outcome: 'settled' },
        ],
      }),
    ).toBe('VLM(vlm 단)이 확정 · 아래 1단은 답했으나 확정 권한 없음')
  })

  it('장부가 없으면 지어내지 않고 그렇게 말한다', () => {
    expect(evidenceSummary({ ...BASE, ledger_recorded: false })).toBe(
      '결정 장부에 이 판정의 기록이 없어 경로를 알 수 없습니다',
    )
  })
})

describe('notableAnnotations', () => {
  it('operative:* 는 «실제로 쓴 결정» 으로 먼저, 나머지는 이름순', () => {
    const rows = notableAnnotations({
      'memory:distance': '0.12',
      'operative:source': 'vlm',
      'count:total': '4',
      'operative:stage': 'advisory',
    })
    expect(rows.map((r) => r.key)).toEqual([
      'operative:source',
      'operative:stage',
      'count:total',
      'memory:distance',
    ])
  })

  it('없으면 빈 목록', () => {
    expect(notableAnnotations(undefined)).toEqual([])
  })
})

describe('tierText', () => {
  it('재판정으로 물을 수 있는 판정원 이름', () => {
    expect(tierText('vlm')).toBe('VLM 다시')
    expect(tierText('oracle')).toBe('더 센 판정자(오라클)')
    expect(tierText('ml')).toBe('전용 ML')
    expect(tierText('memory')).toBe('기억(system 1)')
    expect(tierText('x')).toBe('x')
  })
})

describe('agreementText', () => {
  const base = { answered: 2, differing_tiers: [] as string[], votes: { OK: 3 } }
  it('만장일치', () => {
    expect(agreementText({ ...base, agreement: 'unanimous' })).toBe('답한 2곳 모두 원 판정과 같습니다')
  })
  it('갈림은 다른 판정원을 이름으로', () => {
    expect(
      agreementText({ agreement: 'split', answered: 2, differing_tiers: ['oracle', 'ml'], votes: { OK: 1, NG: 2 } }),
    ).toBe('더 센 판정자(오라클) · 전용 ML 이(가) 원 판정과 다릅니다')
  })
  it('아무도 답하지 못함', () => {
    expect(agreementText({ agreement: 'none_answered', answered: 0, differing_tiers: [], votes: { OK: 1 } })).toBe(
      '답한 판정원이 없습니다',
    )
  })
})

describe('opinionText', () => {
  it('답은 클래스와 신뢰도, 기권은 사유', () => {
    expect(opinionText({ tier: 'vlm', kind: 'vlm', status: 'answered', class: 'NG', confidence: 0.912 })).toBe('NG (91%)')
    expect(opinionText({ tier: 'ml', kind: 'ml', status: 'abstained', reason: 'ml disabled' })).toBe('기권 — ml disabled')
  })
})

describe('votesText', () => {
  it('클래스별 표를 이름순으로', () => {
    expect(votesText({ OK: 1, NG: 2 })).toBe('NG 2 · OK 1')
  })
})
