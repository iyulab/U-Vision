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
