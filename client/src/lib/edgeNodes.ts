import type { EdgeNode } from './types'

/**
 * 엣지 노드 목록의 화면 문구 — 서버가 준 값을 «운영자가 무엇을 봐야 하는가» 로 읽는다. 순수 함수만 둔다(시계는 인자로 받는다).
 *
 * 문턱은 하나뿐이다: 에이전트는 30 초마다 엔진을 부르고 엔진은 마지막 접속 시각을 최대 1 분 간격으로 적으므로, 5 분을 넘기면 «연락이
 * 끊겼다» 로 읽는다(몇 번의 폴링을 놓친 것). 대기 건수에는 문턱을 두지 않는다 — 밀렸다는 것이 몇 건부터인지는 현장이 정한다.
 */
export const STALE_AFTER_MS = 5 * 60_000

export type FlagSeverity = 'bad' | 'warn' | 'info'

export interface NodeFlag {
  severity: FlagSeverity
  text: string
}

export interface NodeRow {
  node: EdgeNode
  revoked: boolean
  /** 가장 심각한 깃발의 심각도(없으면 null) — 줄의 색을 정한다. */
  worst: FlagSeverity | null
  flags: NodeFlag[]
  /** «마지막 연락 3분 전» · «아직 연결된 적 없음». */
  lastSeenText: string
  /** «대기 12건 · 버린 크롭 0» — 보고가 없으면 null. */
  backlogText: string | null
}

/** 밀리초 길이를 «방금 · 12초 · 3분 · 2시간 · 4일» 로. 음수(시계 어긋남)는 방금으로 본다. */
export function formatAge(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000))
  if (s < 5) return '방금'
  if (s < 60) return `${s}초`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}분`
  const h = Math.floor(m / 60)
  if (h < 48) return `${h}시간`
  return `${Math.floor(h / 24)}일`
}

function ageMs(iso: string | undefined, now: number): number | null {
  if (!iso) return null
  const t = Date.parse(iso)
  return Number.isNaN(t) ? null : now - t
}

const RANK: Record<FlagSeverity, number> = { bad: 2, warn: 1, info: 0 }

/** 노드 한 줄을 화면 모양으로. `now` 는 목록을 읽은 시각(밀리초) — 렌더마다 시계를 읽지 않는다. */
export function describeNode(node: EdgeNode, now: number): NodeRow {
  const revoked = node.revoked_at != null
  const flags: NodeFlag[] = []

  if (revoked) {
    flags.push({ severity: 'info', text: '회수됨 — 이 노드의 자격은 더는 통하지 않습니다' })
    return { node, revoked, worst: 'info', flags, lastSeenText: lastSeenOf(node, now), backlogText: null }
  }

  const seenAge = ageMs(node.last_seen, now)
  if (seenAge === null) flags.push({ severity: 'warn', text: '아직 연결된 적 없음 — 등록 토큰으로 에이전트를 등록했는지 확인하세요' })
  else if (seenAge > STALE_AFTER_MS) flags.push({ severity: 'bad', text: `연락이 끊김 — 마지막 연락 ${formatAge(seenAge)} 전` })

  if (node.scenarios.length === 0) flags.push({ severity: 'warn', text: '시나리오 미배정 — 노드가 받는 구성이 비어 있습니다' })

  const status = node.status
  if (status) {
    if (status.parked > 0)
      flags.push({ severity: 'bad', text: `따로 둔 항목 ${status.parked}건 — 엔진이 영구 거절한 사건·크롭이라 사람이 봐야 합니다` })
    if (status.dropped_crops > 0)
      flags.push({ severity: 'warn', text: `버린 크롭 ${status.dropped_crops}건 — 노드 디스크 상한 때문(사건은 버리지 않습니다)` })
    const worker = status.worker
    if (worker) {
      // 규칙이 서 있는데 워커가 안 붙어 있으면 규칙은 영상을 받지 못한다 — 침묵이 아니라 운영자가 볼 깃발이다.
      if (!worker.connected && worker.rules > 0)
        flags.push({ severity: 'bad', text: `추론 워커가 연결돼 있지 않습니다 — 규칙 ${worker.rules}개가 영상을 받지 못합니다` })
      if (worker.skipped_rules > 0)
        flags.push({
          severity: 'bad',
          text: `건너뛴 규칙 ${worker.skipped_rules}개 — ${worker.skipped?.[0] ?? '구성이 가리키는 소스·구역·선이 없습니다'}`,
        })
      if (worker.dropped > 0)
        flags.push({
          severity: 'warn',
          text: `워커가 보낸 줄 ${worker.dropped}개를 버렸습니다${worker.last_drop_reason ? ` — 마지막 사유: ${worker.last_drop_reason}` : ''}`,
        })
    }
    const reportAge = ageMs(status.reported_at, now)
    // 연락은 있는데 보고만 낡았다면 옛 에이전트이거나 보고만 실패하는 것이다 — 연락이 끊긴 노드의 낡은 보고는 위 깃발이 이미 말한다.
    if (seenAge !== null && seenAge <= STALE_AFTER_MS && reportAge !== null && reportAge > STALE_AFTER_MS)
      flags.push({ severity: 'info', text: `상태 보고가 ${formatAge(reportAge)} 전 것입니다` })
  } else if (seenAge !== null && seenAge <= STALE_AFTER_MS) {
    flags.push({ severity: 'info', text: '상태 보고 없음 — 보고를 모르는 옛 에이전트일 수 있습니다' })
  }

  if (node.clock_skewed) {
    const secs = node.clock_offset_ms != null ? Math.round(Math.abs(node.clock_offset_ms) / 1000) : null
    flags.push({ severity: 'warn', text: secs != null ? `시계 어긋남 — 서버와 약 ${secs}초 차이` : '시계 어긋남' })
  }

  let worst: FlagSeverity | null = null
  for (const f of flags) if (worst === null || RANK[f.severity] > RANK[worst]) worst = f.severity

  return {
    node,
    revoked,
    worst,
    flags,
    lastSeenText: lastSeenOf(node, now),
    backlogText: status ? `대기 ${status.outbox_depth}건 · 버린 크롭 ${status.dropped_crops}건 · 따로 둔 ${status.parked}건` : null,
  }
}

function lastSeenOf(node: EdgeNode, now: number): string {
  const age = ageMs(node.last_seen, now)
  return age === null ? '아직 연결된 적 없음' : `마지막 연락 ${formatAge(age)} 전`
}

/** 목록 정렬 — 주의가 필요한 줄을 위로, 회수된 노드는 맨 아래로, 같은 등급은 등록 순을 지킨다. */
export function sortRows(rows: NodeRow[]): NodeRow[] {
  const key = (r: NodeRow) => (r.revoked ? -1 : r.worst === null ? 0 : RANK[r.worst] + 1)
  return rows
    .map((row, index) => ({ row, index }))
    .sort((a, b) => key(b.row) - key(a.row) || a.index - b.index)
    .map((x) => x.row)
}

/** 토큰 발급 입력 검사 — 서버 규칙(이름 80자 이하 제어 문자 없음 · 유효 시간 1~1440 분)을 먼저 알려 준다. */
export function validateTokenInput(name: string, ttlMinutes: number): string | null {
  const trimmed = name.trim()
  if (trimmed.length > 80) return '이름은 80자 이하여야 합니다.'
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(trimmed)) return '이름에 제어 문자를 쓸 수 없습니다.'
  if (!Number.isInteger(ttlMinutes) || ttlMinutes < 1 || ttlMinutes > 1440) return '유효 시간은 1~1440분이어야 합니다.'
  return null
}

/** 선택한 시나리오가 노드에 배정된 것과 다른가(저장 버튼의 활성 조건). 순서는 보지 않는다. */
export function assignmentChanged(current: readonly string[], selected: readonly string[]): boolean {
  if (current.length !== selected.length) return true
  const set = new Set(current)
  return selected.some((id) => !set.has(id))
}
