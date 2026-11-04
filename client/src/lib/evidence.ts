import type { DecisionEvidence } from './types'

/** 경로 단의 결과(서버 어휘, 결과 v2 와 같다)를 사람이 읽는 말로 — 모르는 값은 그대로 보여 준다. */
export function outcomeText(outcome: string): string {
  switch (outcome) {
    case 'settled':
      return '확정'
    case 'shadowed':
      return '답했으나 확정 권한 없음'
    case 'escalated':
      return '놓치면 안 되는 값이라 위로'
    case 'abstained':
      return '기권'
    default:
      return outcome
  }
}

/** 판정자 종류(서버 어휘)를 사람이 읽는 말로. */
export function kindText(kind: string): string {
  switch (kind) {
    case 'vlm':
      return 'VLM'
    case 'ml':
      return '전용 ML'
    case 'system1':
      return 'system 1(기억·대조)'
    case 'human':
      return '사람'
    default:
      return kind
  }
}

/**
 * 근거 한 줄 요약 — 누가 확정했고 아래 단은 어떻게 됐는가. 장부가 없으면 경로를 지어내지 않고 그렇게 말한다(정직성).
 */
export function evidenceSummary(e: DecisionEvidence): string {
  if (!e.ledger_recorded || e.settled_by === undefined)
    return '결정 장부에 이 판정의 기록이 없어 경로를 알 수 없습니다'
  const head = `${kindText(e.settled_by.kind)}(${e.settled_by.tier} 단)이 확정`
  const below = (e.path ?? []).filter((s) => s.tier !== e.settled_by!.tier)
  if (below.length === 0) return head
  const outcomes = [...new Set(below.map((s) => outcomeText(s.outcome)))].join(' · ')
  return `${head} · 아래 ${below.length}단은 ${outcomes}`
}

/** 장부 annotation 을 표시 순서로 — 실제로 쓴 결정(`operative:*`)이 먼저, 나머지는 이름순. */
export function notableAnnotations(
  annotations: Readonly<Record<string, string>> | undefined,
): { key: string; value: string }[] {
  if (annotations === undefined) return []
  return Object.entries(annotations)
    .map(([key, value]) => ({ key, value }))
    .sort((a, b) => {
      const ao = a.key.startsWith('operative:') ? 0 : 1
      const bo = b.key.startsWith('operative:') ? 0 : 1
      return ao - bo || a.key.localeCompare(b.key, 'en')
    })
}
