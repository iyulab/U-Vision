import { formatPercent } from './metrics'
import type { Ambiguity, AmbiguityGroup } from './types'

/**
 * 한 출제 집단의 한 줄 — «답 N · 모르겠음 M · 모르겠음 비율 P %». 비율은 서버가 닫힌 문제가 모자라 내지 않았으면(없음) 건수만 보이고
 * 왜 비었는지 말한다 — 몇 건의 점추정이 시나리오의 모호성처럼 읽히지 않게.
 */
export function groupLine(group: Pick<AmbiguityGroup, 'answered' | 'abstained' | 'open' | 'abstain_rate'>, minAnswers: number): string {
  const parts = [`답 ${group.answered}`, `모르겠음 ${group.abstained}`]
  if (group.open > 0) parts.push(`미답 ${group.open}`)
  const closed = group.answered + group.abstained
  parts.push(
    group.abstain_rate === undefined || group.abstain_rate === null
      ? `비율은 닫힌 ${minAnswers}건부터(지금 ${closed}건)`
      : `모르겠음 비율 ${formatPercent(group.abstain_rate)}`,
  )
  return parts.join(' · ')
}

/** 풀이가 하나도 닫히지 않았으면(문제 큐를 쓰지 않았거나 계수 시나리오) 카드를 그리지 않는다. */
export function hasClosedQuestions(a: Ambiguity | undefined): a is Ambiguity {
  return a !== undefined && a.representative.answered + a.representative.abstained + a.targeted.answered + a.targeted.abstained > 0
}
