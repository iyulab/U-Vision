import { describe, expect, it } from 'vitest'

import {
  MAX_RANGE_DAYS,
  exportAdvice,
  hasAnythingToDelete,
  kindLabel,
  kindRows,
  lossLines,
  previousDay,
  samePlanTarget,
  validateRetention,
} from './retention'
import type { RetentionPurgeResponse } from './types'

const TODAY = '2026-10-31'

function plan(over: Partial<RetentionPurgeResponse> = {}): RetentionPurgeResponse {
  return {
    scenario_id: 's1',
    from: '2026-01-01',
    to: '2026-01-31',
    dry_run: true,
    purged: false,
    plan_fingerprint: 'abc',
    kinds: [{ kind: 'records', days: 31, files: 1200, bytes: 3 * 1024 ** 3 }],
    lost: { results: 600, human_labels: 40, labels_not_exported: 0, open_questions: 0, decisions: 0, metric_rows: 0 },
    ...over,
  }
}

describe('validateRetention', () => {
  const ok = { from: '2026-01-01', to: '2026-01-31', kinds: ['records'] }

  it('올바른 입력은 통과한다', () => {
    expect(validateRetention(ok, TODAY)).toBeNull()
  })

  it('날짜가 비면 거절한다', () => {
    expect(validateRetention({ ...ok, from: '' }, TODAY)).toMatch(/모두 고르세요/)
    expect(validateRetention({ ...ok, to: '' }, TODAY)).toMatch(/모두 고르세요/)
  })

  it('시작일이 종료일보다 늦으면 거절한다', () => {
    expect(validateRetention({ ...ok, from: '2026-02-01', to: '2026-01-01' }, TODAY)).toMatch(/늦습니다/)
  })

  it('오늘과 그 뒤는 구간에 못 든다 — 어제까지는 든다', () => {
    expect(validateRetention({ ...ok, to: '2026-10-30' }, TODAY)).toBeNull()
    expect(validateRetention({ ...ok, to: TODAY }, TODAY)).toMatch(/오늘/)
    expect(validateRetention({ ...ok, to: '2026-11-02' }, TODAY)).toMatch(/오늘/)
  })

  it('부류를 하나도 고르지 않으면 거절한다', () => {
    expect(validateRetention({ ...ok, kinds: [] }, TODAY)).toMatch(/부류/)
  })

  it('상한과 같은 길이(포함 3660일)는 통과하고 하루 더 길면 거절한다', () => {
    const to = '2026-10-30'
    const fromOf = (days: number) => {
      let d = to
      for (let i = 1; i < days; i++) d = previousDay(d)
      return d
    }
    expect(validateRetention({ from: fromOf(MAX_RANGE_DAYS), to, kinds: ['records'] }, TODAY)).toBeNull()
    expect(validateRetention({ from: fromOf(MAX_RANGE_DAYS + 1), to, kinds: ['records'] }, TODAY)).toMatch(String(MAX_RANGE_DAYS))
  })
})

describe('previousDay', () => {
  it('월 · 연 경계를 넘는다', () => {
    expect(previousDay('2026-03-01')).toBe('2026-02-28')
    expect(previousDay('2026-01-01')).toBe('2025-12-31')
    expect(previousDay('2024-03-01')).toBe('2024-02-29')
  })
})

describe('samePlanTarget', () => {
  const req = { from: '2026-01-01', to: '2026-01-31', kinds: ['records'] }

  it('시나리오 · 구간 · 부류가 모두 같을 때만 같다', () => {
    expect(samePlanTarget(plan(), 's1', req)).toBe(true)
    expect(samePlanTarget(plan(), 's2', req)).toBe(false)
    expect(samePlanTarget(plan(), 's1', { ...req, to: '2026-01-30' })).toBe(false)
    expect(samePlanTarget(plan(), 's1', { ...req, kinds: ['records', 'ledger'] })).toBe(false)
  })

  it('부류 순서는 상관없다', () => {
    const p = plan({
      kinds: [
        { kind: 'metrics', days: 1, files: 1, bytes: 1 },
        { kind: 'records', days: 1, files: 1, bytes: 1 },
      ],
    })
    expect(samePlanTarget(p, 's1', { ...req, kinds: ['records', 'metrics'] })).toBe(true)
  })
})

describe('계획 읽기', () => {
  it('파일이 하나도 없으면 지울 것이 없다', () => {
    expect(hasAnythingToDelete(plan())).toBe(true)
    expect(hasAnythingToDelete(plan({ kinds: [{ kind: 'records', days: 0, files: 0, bytes: 0 }] }))).toBe(false)
  })

  it('부류별 줄에 날짜 · 파일 · 용량이 든다', () => {
    expect(kindRows(plan())).toEqual([{ label: '판정 기록', detail: '31일 · 파일 1,200개 · 3.0 GB' }])
  })

  it('모르는 부류는 이름을 그대로 보인다', () => {
    expect(kindLabel('future')).toBe('future')
  })

  it('잃는 것은 0 이 아닌 칸만 문장으로 낸다', () => {
    expect(lossLines(plan())).toEqual(['판정 레코드 600건', '사람 라벨 40건 (모두 데이터셋 export 에 담겨 있습니다)'])
  })

  it('export 에 안 담긴 라벨 수를 숨기지 않는다', () => {
    const lines = lossLines(plan({ lost: { ...plan().lost, labels_not_exported: 12 } }))
    expect(lines.some((l) => l.includes('12건은 어느 데이터셋 export 에도 담기지 않았습니다'))).toBe(true)
  })

  it('장부 · 메트릭 · 열린 문제를 각각 밝힌다', () => {
    const lines = lossLines(
      plan({ lost: { results: 0, human_labels: 0, labels_not_exported: 0, open_questions: 3, decisions: 9, metric_rows: 1500 } }),
    )
    expect(lines).toEqual([
      '열린 문제 3건 — 사진이 없어져 풀 수 없으므로 철회됩니다',
      '결정 장부의 결정 9건 — 관문 재측정의 원천입니다',
      '메트릭 1,500행',
    ])
  })

  it('모두 0 이면 빈 목록', () => {
    expect(lossLines(plan({ lost: { results: 0, human_labels: 0, labels_not_exported: 0, open_questions: 0, decisions: 0, metric_rows: 0 } }))).toEqual([])
  })

  it('export 권고는 라벨이 덮이지 않았다고 서버가 말할 때만 낸다', () => {
    expect(exportAdvice(plan({ covered_by_export: false }))).toMatch(/export/)
    expect(exportAdvice(plan({ covered_by_export: true }))).toBeNull()
    expect(exportAdvice(plan())).toBeNull() // records 를 고르지 않았다 — 서버가 필드를 생략
  })
})
