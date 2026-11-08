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

/**
 * 이 격상에 명시 확인이 필요한가 — 서버(`AuthorityPromotionGuard`)와 같은 규칙의 미러다. 판정은 서버가 하고(근거를 직접 다시 읽는다),
 * 여기서는 «물어볼지» 만 미리 안다: ML 이 판정 권한을 받는 단계(co_primary 이상)로 올리는데 신호가 «적격» 이 아니거나, 한 단계를 건너뛴다.
 * 신호를 아직 못 읽었으면(null) «적격» 이 아니다.
 */
export function needsAcknowledgement(
  current: AuthorityStage,
  target: AuthorityStage,
  signal: 'eligible' | 'unmet' | 'insufficient' | null,
): boolean {
  const skips = AUTHORITY_STAGES.indexOf(target) - AUTHORITY_STAGES.indexOf(current) > 1
  const givesMlAuthority = AUTHORITY_STAGES.indexOf(target) >= AUTHORITY_STAGES.indexOf('co_primary')
  return skips || (givesMlAuthority && signal !== 'eligible')
}

const BASIS_LABEL: Record<string, string> = {
  skipped_stage: '단계를 건너뜀',
  signal_unmet: '격상 신호 «적격 아님»',
  signal_insufficient: '격상 신호 표본 부족',
}

/**
 * 격상 이력 한 줄의 «근거 없이 올림» 표지 — 서버가 확인을 요구했고 사람이 확인한 격상에만 있다(`acknowledged_unverified`). 근거가 있었던
 * 격상 · 격하 · 이 필드가 생기기 전의 옛 이력은 null. 모르는 사유 표지는 지어내지 않고 그대로 보인다.
 */
export function unverifiedMark(h: { acknowledged_unverified?: boolean; unverified_basis?: string[] }): string | null {
  if (!h.acknowledged_unverified) return null
  const why = (h.unverified_basis ?? []).map((b) => BASIS_LABEL[b] ?? b)
  return why.length > 0 ? `근거 없이 올림 — ${why.join(' · ')}` : '근거 없이 올림'
}

export type EvidenceMetrics = {
  labeled_ng?: number
  labeled_ok?: number
  ml_ng_scored?: number
  ml_ng_hits?: number
  ml_ok_scored?: number
  ml_false_alarms?: number
  ml_ng_recall?: number | null
  ml_false_alarm_rate?: number | null
  /** 정상 오경보를 상한으로 묶었을 때의 재현율 — 격상 신호의 재현율 항(D-116). 점수가 붙은 건이 없으면 생략. */
  ml_recall_at_far?: number | null
  ml_auroc?: number | null
  ml_ranked_ng?: number | null
  ml_ranked_ok?: number | null
  promotion_eligible?: boolean | null
  /** 신호가 읽은 ML 모델 판과 그 판의 사람 라벨 표본 — 행이 모델 신원을 싣지 않으면 생략(D-119). */
  promotion_basis?: { model: string; ng_scored: number; ok_scored: number } | null
}

export type EvidenceSummary = {
  /** 격상 신호의 뜻 — 점추정이라 확정이 아니다. */
  signal: 'eligible' | 'unmet' | 'insufficient'
  /** 신호가 읽은 모델 판의 표본 — 서버가 준 것만(없으면 null). 모델을 다시 지으면 새 판이 표본을 채울 때까지 신호가 비는 이유다. */
  basis: { model: string; ngScored: number; okScored: number } | null
  rows: { label: string; value: string }[]
}

const pct = (r: number | null | undefined) => (r == null ? '—' : `${(r * 100).toFixed(1)} %`)
const frac = (a: number | undefined, b: number | undefined) => (b ? `${a ?? 0}/${b}` : '0건')

/** 서버 집계(창)를 격상 확인 화면의 읽기 전용 근거 줄로 바꾼다 — 값을 지어내지 않고 서버가 준 것만 옮긴다. */
export function evidenceSummary(m: EvidenceMetrics): EvidenceSummary {
  const b = m.promotion_basis ?? null
  return {
    signal: m.promotion_eligible === true ? 'eligible' : m.promotion_eligible === false ? 'unmet' : 'insufficient',
    basis: b ? { model: b.model, ngScored: b.ng_scored, okScored: b.ok_scored } : null,
    rows: [
      { label: '사람 라벨 불량 / 정상', value: `${m.labeled_ng ?? 0} / ${m.labeled_ok ?? 0}` },
      { label: 'ML 이 점수 매긴 불량 중 적중 (재현율)', value: `${frac(m.ml_ng_hits, m.ml_ng_scored)} (${pct(m.ml_ng_recall)})` },
      { label: 'ML 이 점수 매긴 정상 중 불량으로 부름 (오경보율)', value: `${frac(m.ml_false_alarms, m.ml_ok_scored)} (${pct(m.ml_false_alarm_rate)})` },
      // 신호의 재현율 항은 위 한 작동점(적중)이 아니라 이 값이다 — 같은 모델도 작동점은 문턱 하나로 크게 갈린다.
      { label: '정상 오경보를 상한으로 묶었을 때 잡은 불량 (신호의 재현율 항)', value: `${pct(m.ml_recall_at_far)} · 점수 표본 불량 ${m.ml_ranked_ng ?? 0} / 정상 ${m.ml_ranked_ok ?? 0}` },
      { label: '순위 품질 (AUROC — 불량 점수가 정상보다 높을 확률)', value: m.ml_auroc == null ? '—' : m.ml_auroc.toFixed(3) },
      // 신호의 ML 쪽 항은 창 전체가 아니라 가장 최근에 답한 모델 판의 표본만 읽는다 — 위 줄들(창 전체)과 다른 표본이다.
      ...(b ? [{ label: '신호가 읽는 모델 판 (이 판의 표본만 잰다)', value: `${b.model.slice(0, 12)} · 불량 ${b.ng_scored} / 정상 ${b.ok_scored}` }] : []),
    ],
  }
}
