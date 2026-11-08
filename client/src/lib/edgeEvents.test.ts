import { describe, expect, it } from 'vitest'

import { describeCurrent, describeEvent } from './edgeEvents'
import type { EdgeEvent, EdgeNodeEvents } from './types'

const T = '2026-11-08T03:00:00+00:00'

function view(current: Partial<EdgeNodeEvents['current']>): EdgeNodeEvents {
  return { node_id: 'n', events: [], scanned: 0, truncated: false, current: { counts: [], alarms: [], sources: [], ...current } }
}

const count = { scenario_id: 's', rule: 'stock', source: 'dock', zone: 'z1', class: 'box', count: 4, since: T, device_time: T, received_at: T }
const source = (state: string) => ({ scenario_id: 's', source: 'dock', camera: 'cam-1', state, device_time: T, received_at: T })

describe('describeCurrent', () => {
  it('shows a count as current while its source is ok (or has never reported)', () => {
    const fresh = describeCurrent(view({ counts: [count] })).counts[0]
    expect(fresh.stale).toBe(false)
    expect(fresh.tone).toBe('ok')
    expect(fresh.text).toContain('4개')

    const ok = describeCurrent(view({ counts: [count], sources: [source('ok')] })).counts[0]
    expect(ok.stale).toBe(false)
  })

  it('keeps the last count but marks it stale when its source lost the signal — never as if it were current', () => {
    const line = describeCurrent(view({ counts: [count], sources: [source('no_signal')] })).counts[0]
    expect(line.stale).toBe(true)
    expect(line.tone).toBe('bad')
    expect(line.text).toContain('4개')
    expect(line.text).toContain('낡은 값')
    expect(line.text).toContain('신호 없음')
  })

  it('reads a degraded source as a warning, and an unknown state as not-ok', () => {
    expect(describeCurrent(view({ counts: [count], sources: [source('degraded')] })).counts[0].tone).toBe('warn')
    const unknown = describeCurrent(view({ counts: [count], sources: [source('weird')] })).counts[0]
    expect(unknown.stale).toBe(true)
    expect(unknown.text).toContain('알 수 없는 상태')
  })

  it('does not mix up sources of different scenarios', () => {
    const other = { ...source('no_signal'), scenario_id: 'other' }
    expect(describeCurrent(view({ counts: [count], sources: [other] })).counts[0].stale).toBe(false)
  })

  it('shows a raised alarm as bad and flags a stale alarm state', () => {
    const alarm = { scenario_id: 's', rule: 'low', source: 'dock', zone: 'z1', class: 'box', state: 'raised', count: 1, device_time: T, received_at: T }
    const raised = describeCurrent(view({ alarms: [alarm] })).alarms[0]
    expect(raised.tone).toBe('bad')
    expect(raised.text).toContain('경보 올라가 있음')

    const cleared = describeCurrent(view({ alarms: [{ ...alarm, state: 'cleared' }], sources: [source('no_signal')] })).alarms[0]
    expect(cleared.stale).toBe(true)
    expect(cleared.text).toContain('경보 해제됨')
    expect(cleared.text).toContain('낡은 상태')
  })

  it('marks a count that was produced with a stale configuration', () => {
    const line = describeCurrent(view({ counts: [{ ...count, config_stale: true }] })).counts[0]
    expect(line.tone).toBe('warn')
    expect(line.text).toContain('구성이 낡은 채')
  })

  it('lists every source with its state', () => {
    const lines = describeCurrent(view({ sources: [source('no_signal')] })).sources
    expect(lines[0].text).toContain('cam-1')
    expect(lines[0].text).toContain('신호 없음')
    expect(lines[0].tone).toBe('bad')
  })
})

describe('describeEvent', () => {
  const base = { node_id: 'n', event_id: 'e1', seq: 1, scenario_id: 's', device_time: T, received_at: T }
  const ev = (kind: string, data: Record<string, unknown>): EdgeEvent => ({ ...base, kind, data })

  it('describes the rule kinds in one line each', () => {
    expect(describeEvent(ev('count_changed', { rule: 'stock', previous: 4, count: 6 })).text).toBe('stock · 개수 4 → 6')
    expect(describeEvent(ev('count_changed', { rule: 'stock', previous: null, count: 4 })).text).toBe('stock · 개수 처음 → 4')
    expect(describeEvent(ev('alarm_raised', { rule: 'low', count: 1 })).tone).toBe('bad')
    expect(describeEvent(ev('alarm_cleared', { rule: 'low', count: 5 })).text).toContain('경보 해제')
    expect(describeEvent(ev('count_changed', { rule: 'gate', direction: 'in', in: 3, out: 1 })).text).toContain('선 통과 들어옴')
  })

  it('shows a lost signal as bad and an unknown kind as its own name', () => {
    const lost = describeEvent(ev('source_state', { camera: 'cam-1', previous: 'ok', state: 'no_signal' }))
    expect(lost.tone).toBe('bad')
    expect(lost.text).toBe('cam-1 · 정상 → 신호 없음')
    expect(describeEvent(ev('mystery', {})).text).toBe('mystery')
  })

  it('survives an event without data', () => {
    expect(describeEvent({ ...base, kind: 'count_changed' }).text).toContain('개수')
  })
})
