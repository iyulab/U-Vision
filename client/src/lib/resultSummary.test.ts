import { describe, expect, it } from 'vitest'

import { percent, summarizeResults } from './resultSummary'
import type { StoredLabel, StoredResult } from './types'

const result = (image_id: string, verdict: 'OK' | 'NG'): StoredResult =>
  ({ image_id, verdict, findings: '', confidence: 1, timestamp: 't', scenario_id: 's', image_file: `${image_id}.jpg`, device_id: '', device_label: '' }) as StoredResult

const label = (image_id: string, l: string): StoredLabel => ({ image_id, label: l, timestamp: 't' })

describe('summarizeResults', () => {
  it('판정 수 · NG 비율 · 사람 라벨과의 불일치를 센다', () => {
    const results = [result('a', 'OK'), result('b', 'NG'), result('c', 'OK'), result('d', 'OK')]
    const labels = new Map([
      ['a', label('a', 'OK')],
      ['c', label('c', 'NG')], // 사람이 OK 판정을 NG 로 고침
    ])

    expect(summarizeResults(results, labels)).toEqual({ total: 4, ok: 3, ng: 1, ngRate: 0.25, labeled: 2, corrected: 1 })
  })

  it('라벨 이력은 최신 사람 라벨을 쓴다(오라클 소견은 세지 않는다)', () => {
    const l: StoredLabel = {
      image_id: 'a',
      label: 'OK',
      timestamp: 't',
      history: [
        { label: 'NG', by: 'd', at: 't1', mode: 'label' },
        { label: 'OK', by: 'oracle', at: 't2', mode: 'oracle' },
      ],
    }
    expect(summarizeResults([result('a', 'OK')], new Map([['a', l]])).corrected).toBe(1)
  })

  it('기록이 없으면 비율은 null', () => {
    expect(summarizeResults([], new Map()).ngRate).toBeNull()
  })
})

describe('percent', () => {
  it('소수 한 자리, 정수면 소수 없이', () => {
    expect(percent(0.25)).toBe('25%')
    expect(percent(1 / 3)).toBe('33.3%')
    expect(percent(0)).toBe('0%')
  })
})
