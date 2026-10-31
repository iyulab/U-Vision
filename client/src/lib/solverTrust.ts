import { formatPercent } from './metrics'
import type { SolverTrust, SolverTrustDevice } from './types'

/**
 * 풀이자 한 곳의 한 줄 — «정답 N · 오답 M · 모르겠음 K · 정답률 P %». 정답률은 서버가 답한 건수가 모자라 내지 않았으면(없음) 건수만 보이고
 * 왜 비었는지 말한다 — 몇 건의 점추정이 풀이자 신뢰처럼 읽히지 않게.
 */
export function deviceLine(d: Pick<SolverTrustDevice, 'correct' | 'wrong' | 'unsure' | 'open' | 'accuracy'>, minAnswers: number): string {
  const parts = [`정답 ${d.correct}`, `오답 ${d.wrong}`, `모르겠음 ${d.unsure}`]
  if (d.open > 0) parts.push(`미답 ${d.open}`)
  const answered = d.correct + d.wrong
  parts.push(
    d.accuracy === undefined || d.accuracy === null
      ? `정답률은 답한 ${minAnswers}건부터(지금 ${answered}건)`
      : `정답률 ${formatPercent(d.accuracy)}`,
  )
  return parts.join(' · ')
}

/** 심은 문제가 하나도 나가지 않았으면(기본) 카드를 그리지 않는다. */
export function hasPlants(trust: SolverTrust | undefined): trust is SolverTrust {
  return trust !== undefined && trust.planted > 0
}
