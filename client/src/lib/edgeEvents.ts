import type { EdgeCurrentAlarm, EdgeCurrentCount, EdgeCurrentSource, EdgeEvent, EdgeNodeEvents } from './types'

/**
 * 노드가 보낸 사건의 화면 문구 — 서버가 사건에서 파생한 «지금 상태» 를 운영자가 «지금 믿어도 되는가» 로 읽는다. 순수 함수만 둔다.
 *
 * 핵심 규칙 하나: 센 값이나 경보 상태는 **같은 소스가 `ok` 가 아니면 낡은 값**이다. 영상이 끊긴 뒤에도 마지막으로 센 개수가 «정상» 으로
 * 남아 보이면 소리 없는 실패다 — 값을 지우지 않고 «마지막으로 센 값, 낡음» 을 붙여 보인다(사건 장부의 기록이므로 지우지 않는다).
 */
export type Tone = 'ok' | 'warn' | 'bad' | 'muted'

export interface CurrentLine {
  key: string
  text: string
  tone: Tone
  /** 소스가 `ok` 가 아니라 값이 낡았다. */
  stale: boolean
}

export interface CurrentView {
  counts: CurrentLine[]
  alarms: CurrentLine[]
  sources: CurrentLine[]
}

export interface EventLine {
  key: string
  time: string
  text: string
  tone: Tone
}

const SOURCE_STATE_TEXT: Record<string, string> = { ok: '정상', no_signal: '신호 없음', degraded: '검출 이상' }

/** 알려지지 않은 상태 문자열은 그대로 보이되 정상으로 읽지 않는다. */
function sourceStateText(state: string): string {
  return SOURCE_STATE_TEXT[state] ?? `알 수 없는 상태(${state})`
}

function sourceTone(state: string): Tone {
  if (state === 'ok') return 'ok'
  if (state === 'degraded') return 'warn'
  return 'bad'
}

/** 같은 시나리오의 같은 소스 상태 — 사건이 한 번도 없었던 소스는 목록에 없고, 그것은 처음부터 정상으로 본다. */
function stateOfSource(sources: readonly EdgeCurrentSource[], scenarioId: string, source: string | null | undefined): string {
  if (!source) return 'ok'
  return sources.find((s) => s.scenario_id === scenarioId && s.source === source)?.state ?? 'ok'
}

/** ISO 시각 → 현지 시:분:초. 읽을 수 없으면 원문을 그대로(숨기지 않는다). */
export function clock(iso: string | null | undefined): string {
  if (!iso) return ''
  const t = Date.parse(iso)
  if (Number.isNaN(t)) return iso
  return new Date(t).toLocaleTimeString('ko-KR', { hour12: false })
}

function where(zone: string | null | undefined, cls: string | null | undefined): string {
  return `${zone ? `구역 ${zone}` : '프레임 전체'}${cls ? ` · ${cls}` : ''}`
}

function describeCount(c: EdgeCurrentCount, sources: readonly EdgeCurrentSource[]): CurrentLine {
  const state = stateOfSource(sources, c.scenario_id, c.source)
  const stale = state !== 'ok'
  const parts = [`${c.rule} · ${where(c.zone, c.class)}: ${c.count}개`, `${clock(c.since ?? c.device_time)} 부터`]
  if (stale) parts.push(`낡은 값 — 소스 ${sourceStateText(state)}, 마지막으로 센 값`)
  if (c.config_stale) parts.push('구성이 낡은 채 낸 값')
  return { key: `${c.scenario_id}/${c.rule}`, text: parts.join(' · '), tone: stale ? sourceTone(state) : c.config_stale ? 'warn' : 'ok', stale }
}

function describeAlarm(a: EdgeCurrentAlarm, sources: readonly EdgeCurrentSource[]): CurrentLine {
  const state = stateOfSource(sources, a.scenario_id, a.source)
  const stale = state !== 'ok'
  const raised = a.state === 'raised'
  const parts = [`${a.rule} · ${where(a.zone, a.class)}: ${raised ? '경보 올라가 있음' : '경보 해제됨'}`, `${clock(a.device_time)}`]
  if (a.count != null) parts.push(`그때 개수 ${a.count}`)
  if (stale) parts.push(`낡은 상태 — 소스 ${sourceStateText(state)}`)
  if (a.config_stale) parts.push('구성이 낡은 채 낸 사건')
  return { key: `${a.scenario_id}/${a.rule}`, text: parts.join(' · '), tone: raised ? 'bad' : stale ? sourceTone(state) : 'ok', stale }
}

function describeSource(s: EdgeCurrentSource): CurrentLine {
  return {
    key: `${s.scenario_id}/${s.camera ?? s.source ?? ''}`,
    text: `${s.camera ?? s.source ?? '소스'}: ${sourceStateText(s.state)} · ${clock(s.device_time)} 부터`,
    tone: sourceTone(s.state),
    stale: false,
  }
}

/** 서버가 파생한 «지금 상태» 를 줄로. 값이 하나도 없으면 세 목록이 모두 비어 있다 — 호출부가 «아직 사건이 없습니다» 를 보인다. */
export function describeCurrent(view: EdgeNodeEvents): CurrentView {
  const { counts, alarms, sources } = view.current
  return {
    counts: counts.map((c) => describeCount(c, sources)),
    alarms: alarms.map((a) => describeAlarm(a, sources)),
    sources: sources.map(describeSource),
  }
}

function field(e: EdgeEvent, name: string): string | number | null {
  const v = (e.data as Record<string, unknown> | null | undefined)?.[name]
  return typeof v === 'string' || typeof v === 'number' ? v : null
}

/** 사건 한 줄 — 종류별 한 문장. 모르는 종류는 종류 이름과 함께 그대로 보인다. */
export function describeEvent(e: EdgeEvent): EventLine {
  const rule = field(e, 'rule')
  const stale = (e.data as Record<string, unknown> | null | undefined)?.config_stale === true ? ' · 구성이 낡은 채' : ''
  let text: string
  let tone: Tone = 'muted'
  switch (e.kind) {
    case 'count_changed':
      if (field(e, 'direction') != null) {
        text = `${rule ?? '규칙'} · 선 통과 ${field(e, 'direction') === 'in' ? '들어옴' : '나감'} (누적 들어옴 ${field(e, 'in') ?? '?'} · 나감 ${field(e, 'out') ?? '?'})`
      } else {
        const prev = field(e, 'previous')
        text = `${rule ?? '규칙'} · 개수 ${prev == null ? '처음' : prev} → ${field(e, 'count') ?? '?'}`
      }
      break
    case 'alarm_raised':
      text = `${rule ?? '규칙'} · 경보 올림 (개수 ${field(e, 'count') ?? '?'})`
      tone = 'bad'
      break
    case 'alarm_cleared':
      text = `${rule ?? '규칙'} · 경보 해제 (개수 ${field(e, 'count') ?? '?'})`
      tone = 'ok'
      break
    case 'source_state': {
      const state = String(field(e, 'state') ?? '?')
      text = `${field(e, 'camera') ?? field(e, 'source') ?? '소스'} · ${sourceStateText(String(field(e, 'previous') ?? 'ok'))} → ${sourceStateText(state)}`
      tone = sourceTone(state)
      break
    }
    default:
      text = e.kind
  }
  return { key: e.event_id, time: clock(e.device_time), text: text + stale, tone }
}
