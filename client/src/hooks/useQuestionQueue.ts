import { useCallback, useState } from 'react'

import { answerQuestion, getNextQuestion } from '../lib/api'
import { answerPayload, isClosedQuestion, questionErrorText, type QuestionPick } from '../lib/question'
import type { Question } from '../lib/types'
import { useResource } from './useResource'

export type QueueView =
  | { phase: 'loading' }
  | { phase: 'ready'; question: Question }
  | { phase: 'empty' }
  | { phase: 'error'; message: string }

export interface QuestionQueue {
  view: QueueView
  /** 답을 보내는 중(버튼을 잠근다). */
  busy: boolean
  /** 이 화면에서 지금까지 푼 문제 수(예 · 아니오 · 모르겠음 모두). */
  solved: number
  /** 답을 보내다 실패한 이유 — 문제는 그대로 남아 다시 누를 수 있다. */
  answerError: string | null
  answer: (pick: QuestionPick) => Promise<void>
  /** 다음 문제를 다시 받는다(비었거나 불러오기에 실패했을 때). */
  retry: () => void
}

const LOAD_FAILED = '문제를 불러오지 못했습니다. 네트워크를 확인하고 다시 시도하세요.'

/**
 * 한 번에 한 문제씩 푸는 큐 — 받기 → 답하기 → 다음 받기. 상태 전이만 맡는다: 사진을 어떻게 고르는지는 서버의 일이고, 이 훅은
 * 서버가 준 문제를 그대로 보인다. 시나리오가 바뀌면 키가 바뀌어 새로 읽는다(이전 시나리오의 문제는 보이지 않는다).
 */
export function useQuestionQueue(scenarioId: string, deviceId: string): QuestionQueue {
  const next = useResource(
    `${scenarioId}|${deviceId}`,
    () => getNextQuestion(scenarioId, deviceId),
    LOAD_FAILED,
  )
  const [busy, setBusy] = useState(false)
  const [solved, setSolved] = useState(0)
  const [answerError, setAnswerError] = useState<string | null>(null)

  const view: QueueView = next.data
    ? { phase: 'ready', question: next.data }
    : next.data === null
      ? { phase: 'empty' }
      : next.error
        ? { phase: 'error', message: LOAD_FAILED }
        : { phase: 'loading' }

  const { reload } = next
  const question = next.data ?? null

  const answer = useCallback(
    async (pick: QuestionPick) => {
      if (!question || busy) return
      setBusy(true)
      setAnswerError(null)
      try {
        await answerQuestion(answerPayload(scenarioId, question.question_id, pick, deviceId))
        setSolved((n) => n + 1)
      } catch (e) {
        // 이미 닫힌 문제(다른 곳에서 답했거나 사진에 라벨이 붙음)는 멈추지 않고 다음 문제로 넘어간다.
        if (!isClosedQuestion(e)) {
          setAnswerError(questionErrorText(e))
          setBusy(false)
          return
        }
      }
      await reload()
      setBusy(false)
    },
    [question, busy, scenarioId, deviceId, reload],
  )

  const retry = useCallback(() => {
    setAnswerError(null)
    void reload()
  }, [reload])

  return { view, busy, solved, answerError, answer, retry }
}
