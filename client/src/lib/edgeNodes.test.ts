import { describe, expect, it } from 'vitest'

import { STALE_AFTER_MS, assignmentChanged, describeNode, formatAge, sortRows, validateTokenInput } from './edgeNodes'
import type { EdgeNode } from './types'

const NOW = Date.parse('2026-11-04T12:00:00Z')
const ago = (ms: number) => new Date(NOW - ms).toISOString()

function node(over: Partial<EdgeNode> = {}): EdgeNode {
  return {
    node_id: 'node_aaaa',
    name: '3라인',
    registered_at: ago(86_400_000),
    scenarios: ['line1'],
    last_seen: ago(20_000),
    status: { reported_at: ago(20_000), outbox_depth: 0, outbox_bytes: 0, dropped_crops: 0, parked: 0 },
    ...over,
  }
}

describe('formatAge', () => {
  it.each([
    [0, '방금'],
    [4_000, '방금'],
    [12_000, '12초'],
    [90_000, '1분'],
    [3 * 3_600_000, '3시간'],
    [47 * 3_600_000, '47시간'],
    [3 * 86_400_000, '3일'],
    [-5_000, '방금'], // 노드 시계가 앞서면 음수 — 방금으로 본다
  ])('%d ms → %s', (ms, text) => expect(formatAge(ms)).toBe(text))
})

function workerNode(worker: Partial<NonNullable<NonNullable<EdgeNode['status']>['worker']>>, over: Partial<EdgeNode> = {}): EdgeNode {
  return node({
    status: {
      reported_at: ago(20_000), outbox_depth: 0, outbox_bytes: 0, dropped_crops: 0, parked: 0,
      worker: { connected: true, frames: 100, dropped: 0, rules: 2, skipped_rules: 0, ...worker },
    },
    ...over,
  })
}

describe('describeNode — inference worker', () => {
  it('a connected worker with working rules adds no flags', () => {
    expect(describeNode(workerNode({}), NOW).flags).toEqual([])
  })

  it('flags a worker that is not connected while rules are waiting for its video', () => {
    const row = describeNode(workerNode({ connected: false, rules: 3 }), NOW)
    expect(row.flags).toEqual([{ severity: 'bad', text: '추론 워커가 연결돼 있지 않습니다 — 규칙 3개가 영상을 받지 못합니다' }])
    expect(row.worst).toBe('bad')
  })

  it('does not flag a missing worker when there are no rules to feed', () => {
    expect(describeNode(workerNode({ connected: false, rules: 0 }), NOW).flags).toEqual([])
  })

  it('names the skipped rules and the dropped lines with their reason', () => {
    const row = describeNode(
      workerNode({ skipped_rules: 1, skipped: ['wh/ghost: 소스 nope 가 없다'], dropped: 4, last_drop_reason: 'JSON 이 아니다' }),
      NOW,
    )
    expect(row.flags.map((f) => f.text)).toEqual([
      '건너뛴 규칙 1개 — wh/ghost: 소스 nope 가 없다',
      '워커가 보낸 줄 4개를 버렸습니다 — 마지막 사유: JSON 이 아니다',
    ])
    expect(row.flags.map((f) => f.severity)).toEqual(['bad', 'warn'])
  })

  it('a node that reports no worker at all (no socket configured) is unchanged', () => {
    expect(describeNode(node(), NOW).flags).toEqual([])
  })
})

describe('describeNode', () => {
  it('a healthy connected node has no flags and shows the backlog line', () => {
    const row = describeNode(node(), NOW)
    expect(row.flags).toEqual([])
    expect(row.worst).toBeNull()
    expect(row.lastSeenText).toBe('마지막 연락 20초 전')
    expect(row.backlogText).toBe('대기 0건 · 버린 크롭 0건 · 따로 둔 0건')
  })

  it('parked items are the loudest flag — a human must look', () => {
    const row = describeNode(node({ status: { reported_at: ago(1000), outbox_depth: 3, outbox_bytes: 10, dropped_crops: 2, parked: 4 } }), NOW)
    expect(row.worst).toBe('bad')
    expect(row.flags.map((f) => f.severity)).toEqual(['bad', 'warn'])
    expect(row.flags[0].text).toContain('4건')
    expect(row.flags[1].text).toContain('버린 크롭 2건')
  })

  it('silence past the threshold is "connection lost", not a backlog', () => {
    const row = describeNode(node({ last_seen: ago(STALE_AFTER_MS + 60_000) }), NOW)
    expect(row.worst).toBe('bad')
    expect(row.flags[0].text).toBe('연락이 끊김 — 마지막 연락 6분 전')
    // 끊긴 노드의 낡은 보고는 따로 말하지 않는다.
    expect(row.flags.some((f) => f.text.startsWith('상태 보고가'))).toBe(false)
  })

  it('a node that never connected is a warning with the way out', () => {
    const row = describeNode(node({ last_seen: undefined, status: undefined, scenarios: [] }), NOW)
    expect(row.lastSeenText).toBe('아직 연결된 적 없음')
    expect(row.flags.map((f) => f.severity)).toEqual(['warn', 'warn'])
    expect(row.backlogText).toBeNull()
  })

  it('no assignment is flagged because the node would receive an empty configuration', () => {
    expect(describeNode(node({ scenarios: [] }), NOW).flags.map((f) => f.text)).toEqual([
      '시나리오 미배정 — 노드가 받는 구성이 비어 있습니다',
    ])
  })

  it('a connected node with no status report hints at an older agent; a stale report alone is info', () => {
    expect(describeNode(node({ status: undefined }), NOW).flags[0]).toMatchObject({ severity: 'info' })
    const stale = describeNode(node({ status: { reported_at: ago(STALE_AFTER_MS + 1000), outbox_depth: 0, outbox_bytes: 0, dropped_crops: 0, parked: 0 } }), NOW)
    expect(stale.worst).toBe('info')
    expect(stale.flags[0].text).toContain('상태 보고가 5분')
  })

  it('clock skew is a warning with the size, or without when the offset is missing', () => {
    expect(describeNode(node({ clock_skewed: true, clock_offset_ms: -600_000 }), NOW).flags[0].text).toBe('시계 어긋남 — 서버와 약 600초 차이')
    expect(describeNode(node({ clock_skewed: true }), NOW).flags[0].text).toBe('시계 어긋남')
    expect(describeNode(node({ clock_skewed: false, clock_offset_ms: 200 }), NOW).flags).toEqual([])
  })

  it('a revoked node says so and nothing else', () => {
    const row = describeNode(node({ revoked_at: ago(1000), last_seen: ago(STALE_AFTER_MS * 10) }), NOW)
    expect(row.revoked).toBe(true)
    expect(row.flags).toHaveLength(1)
    expect(row.backlogText).toBeNull()
  })

  it('an unparseable timestamp is treated as unknown instead of crashing', () => {
    expect(describeNode(node({ last_seen: 'not-a-date' }), NOW).lastSeenText).toBe('아직 연결된 적 없음')
  })
})

describe('sortRows', () => {
  it('puts the loudest first, healthy next, revoked last, and keeps registration order within a level', () => {
    const rows = [
      describeNode(node({ node_id: 'ok1' }), NOW),
      describeNode(node({ node_id: 'rev', revoked_at: ago(1) }), NOW),
      describeNode(node({ node_id: 'bad1', last_seen: ago(STALE_AFTER_MS * 2) }), NOW),
      describeNode(node({ node_id: 'ok2' }), NOW),
      describeNode(node({ node_id: 'warn1', scenarios: [] }), NOW),
      describeNode(node({ node_id: 'bad2', status: { reported_at: ago(1), outbox_depth: 0, outbox_bytes: 0, dropped_crops: 0, parked: 1 } }), NOW),
    ]
    expect(sortRows(rows).map((r) => r.node.node_id)).toEqual(['bad1', 'bad2', 'warn1', 'ok1', 'ok2', 'rev'])
  })
})

describe('validateTokenInput', () => {
  it('accepts a normal request and mirrors the server limits', () => {
    expect(validateTokenInput('3라인 젯슨', 60)).toBeNull()
    expect(validateTokenInput('', 1)).toBeNull()
    expect(validateTokenInput('x'.repeat(81), 60)).toContain('80자')
    expect(validateTokenInput('a\nb', 60)).toContain('제어 문자')
    expect(validateTokenInput('ok', 0)).toContain('1~1440')
    expect(validateTokenInput('ok', 1441)).toContain('1~1440')
    expect(validateTokenInput('ok', 1.5)).toContain('1~1440')
  })
})

describe('assignmentChanged', () => {
  it('ignores order and compares membership', () => {
    expect(assignmentChanged(['a', 'b'], ['b', 'a'])).toBe(false)
    expect(assignmentChanged(['a'], ['a', 'b'])).toBe(true)
    expect(assignmentChanged(['a', 'b'], ['a', 'c'])).toBe(true)
    expect(assignmentChanged([], [])).toBe(false)
  })
})
