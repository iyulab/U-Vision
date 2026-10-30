import { ApiError } from './api'
import type { QuestionAnswerInput } from './contract.gen'

/** 풀이자가 고른 것 — 클래스 하나, 또는 «모르겠음»(라벨이 아니라 기권으로 기록된다). */
export type QuestionPick = { kind: 'class'; id: string } | { kind: 'abstain' }

/** 답 제출 본문(snake_case wire). 클래스와 기권은 서로 배타 — 서버도 정확히 하나만 받는다. */
export function answerPayload(
  scenarioId: string,
  questionId: string,
  pick: QuestionPick,
  by: string,
): QuestionAnswerInput {
  const base = { scenario_id: scenarioId, question_id: questionId, by }
  return pick.kind === 'class' ? { ...base, choice: pick.id } : { ...base, abstain: true }
}

/**
 * 이 문제는 더 답할 수 없다(이미 답했거나, 그 사이 사진에 라벨이 붙었거나, 문제가 사라짐) — 오류로 멈추지 않고 다음
 * 문제로 넘어가면 된다.
 */
export function isClosedQuestion(e: unknown): boolean {
  return e instanceof ApiError && (e.status === 409 || e.status === 404)
}

/** 작업자에게 보일 오류 문장 — 서버 응답 본문(영문 · 내부 정보 가능)은 보이지 않는다. */
export function questionErrorText(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 400) return '이 시나리오는 사진 확인 문제를 낼 수 없습니다.'
    if (e.status >= 500) return '서버에서 문제를 처리하지 못했습니다. 잠시 뒤 다시 시도하세요.'
    return '문제를 처리하지 못했습니다.'
  }
  return '서버에 연결할 수 없습니다. 네트워크를 확인하고 다시 시도하세요.'
}

/** 눌렀을 때 쓸 버튼 색 — «정상» 은 초록, 그 밖의 클래스는 주의색, 기권은 중립. 클래스 이름은 대소문자를 무시한다. */
export function choiceTone(id: string): 'ok' | 'defect' {
  return id.toLowerCase() === 'ok' ? 'ok' : 'defect'
}
