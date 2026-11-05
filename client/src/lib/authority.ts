/** 권한 이양 단계(A1) — 서버 AuthorityStage 미러. 서수: shadow<advisory<co_primary<ml_primary. */
export type AuthorityStage = 'shadow' | 'advisory' | 'co_primary' | 'ml_primary'

export const AUTHORITY_STAGES: AuthorityStage[] = ['shadow', 'advisory', 'co_primary', 'ml_primary']

const LABEL: Record<AuthorityStage, string> = {
  shadow: '그림자(기록만)',
  advisory: '의견(현재 기본)',
  co_primary: '동등 — 불일치 시 차단',
  ml_primary: 'ML 주판정',
}

export function stageLabel(stage: AuthorityStage): string {
  return LABEL[stage]
}

/** 한 단계 위(격상 대상). 최상위면 null. */
export function nextStageUp(stage: AuthorityStage): AuthorityStage | null {
  const i = AUTHORITY_STAGES.indexOf(stage)
  return i < 0 || i >= AUTHORITY_STAGES.length - 1 ? null : AUTHORITY_STAGES[i + 1]
}

/** 격상 근거로 읽는 집계 창의 날 수 — 서버 `GET /metrics?days=` 의 창. 날짜 하나로는 날마다 몇 건씩 쌓이는 불량이 표본 하한에 닿지 못한다. */
export const EVIDENCE_WINDOW_DAYS = 30

export type EvidenceMetrics = {
  labeled_ng?: number
  labeled_ok?: number
  ml_ng_scored?: number
  ml_ng_hits?: number
  ml_ok_scored?: number
  ml_false_alarms?: number
  ml_ng_recall?: number | null
  ml_false_alarm_rate?: number | null
  promotion_eligible?: boolean | null
}

export type EvidenceSummary = {
  /** 격상 신호의 뜻 — 점추정이라 확정이 아니다. */
  signal: 'eligible' | 'unmet' | 'insufficient'
  rows: { label: string; value: string }[]
}

const pct = (r: number | null | undefined) => (r == null ? '—' : `${(r * 100).toFixed(1)} %`)
const frac = (a: number | undefined, b: number | undefined) => (b ? `${a ?? 0}/${b}` : '0건')

/** 서버 집계(창)를 격상 확인 화면의 읽기 전용 근거 줄로 바꾼다 — 값을 지어내지 않고 서버가 준 것만 옮긴다. */
export function evidenceSummary(m: EvidenceMetrics): EvidenceSummary {
  return {
    signal: m.promotion_eligible === true ? 'eligible' : m.promotion_eligible === false ? 'unmet' : 'insufficient',
    rows: [
      { label: '사람 라벨 불량 / 정상', value: `${m.labeled_ng ?? 0} / ${m.labeled_ok ?? 0}` },
      { label: 'ML 이 점수 매긴 불량 중 적중 (재현율)', value: `${frac(m.ml_ng_hits, m.ml_ng_scored)} (${pct(m.ml_ng_recall)})` },
      { label: 'ML 이 점수 매긴 정상 중 불량으로 부름 (오경보율)', value: `${frac(m.ml_false_alarms, m.ml_ok_scored)} (${pct(m.ml_false_alarm_rate)})` },
    ],
  }
}
