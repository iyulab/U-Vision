import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ApiError } from '../lib/api'
import type { Question, QuestionAnswer } from '../lib/types'
import { useQuestionQueue } from './useQuestionQueue'

// 훅의 상태 전이만 본다 — 서버 호출은 바꿔 끼운다.
const h = vi.hoisted(() => ({
  next: vi.fn<(scenarioId: string, by: string) => Promise<Question | null>>(),
  answer: vi.fn<(input: unknown) => Promise<QuestionAnswer>>(),
}))

vi.mock('../lib/api', async (orig) => ({
  ...(await orig<typeof import('../lib/api')>()),
  getNextQuestion: (s: string, by: string) => h.next(s, by),
  answerQuestion: (input: unknown) => h.answer(input),
}))

const q = (id: string): Question => ({
  question_id: id, scenario_id: 's', date: '2026-10-31', image_id: `img-${id}`,
  form: 'single', choices: ['OK', 'NG'], reason: 'random', created_at: '2026-10-31T00:00:00Z',
})

// vitest 전역(globals)을 켜지 않아 RTL 의 자동 정리가 걸리지 않는다.
afterEach(cleanup)

beforeEach(() => {
  h.next.mockReset()
  h.answer.mockReset()
  h.answer.mockResolvedValue({ question_id: 'x', recorded: 'label' })
})

type Queue = ReturnType<typeof useQuestionQueue>
const render = () => renderHook(() => useQuestionQueue('s', 'dev'))

/**
 * 답을 발사만 한다. `await act(() => answer())` 는 쓰지 않는다 — 답은 끝에 다음 문제를 다시 읽는데(`reload`), 그 읽기는 상태
 * 갱신 뒤의 effect 가 시작하고, act 는 콜백이 끝나야 그 갱신을 흘려보내므로 서로를 기다리며 멈춘다. 결과는 `waitFor` 로 본다.
 */
function submit(result: { current: Queue }, pick: Parameters<Queue['answer']>[0]) {
  act(() => { void result.current.answer(pick) })
}

async function ready(result: { current: Queue }) {
  await waitFor(() => expect(result.current.view.phase).toBe('ready'))
}

describe('useQuestionQueue', () => {
  it('처음에는 불러오는 중, 문제가 오면 ready — 장치 id 로 받는다', async () => {
    h.next.mockResolvedValue(q('a'))
    const { result } = render()
    expect(result.current.view.phase).toBe('loading')

    await ready(result)
    expect(result.current.view).toEqual({ phase: 'ready', question: q('a') })
    expect(h.next).toHaveBeenCalledWith('s', 'dev')
  })

  it('물을 사진이 없으면(null) empty, 다시 확인하면 새 문제를 받는다', async () => {
    h.next.mockResolvedValueOnce(null).mockResolvedValueOnce(q('a'))
    const { result } = render()
    await waitFor(() => expect(result.current.view.phase).toBe('empty'))

    act(() => result.current.retry())
    await ready(result)
  })

  it('불러오기가 실패하면 error — 서버 응답 본문을 보이지 않는다', async () => {
    h.next.mockRejectedValue(new ApiError(500, '다음 문제 실패 (500) secret detail'))
    const { result } = render()

    await waitFor(() => expect(result.current.view.phase).toBe('error'))
    expect(JSON.stringify(result.current.view)).not.toContain('secret')
  })

  it('클래스로 답하면 choice 를 보내고, 푼 수를 세고, 다음 문제를 받는다', async () => {
    h.next.mockResolvedValueOnce(q('a')).mockResolvedValueOnce(q('b'))
    const { result } = render()
    await ready(result)

    submit(result, { kind: 'class', id: 'NG' })

    await waitFor(() => expect(result.current.view).toEqual({ phase: 'ready', question: q('b') }))
    expect(h.answer).toHaveBeenCalledWith({ scenario_id: 's', question_id: 'a', by: 'dev', choice: 'NG' })
    await waitFor(() => expect(result.current.busy).toBe(false))
    expect(result.current.solved).toBe(1)
  })

  it('모르겠음은 abstain 을 보내고 마찬가지로 푼 것으로 센다', async () => {
    h.next.mockResolvedValueOnce(q('a')).mockResolvedValueOnce(null)
    const { result } = render()
    await ready(result)

    submit(result, { kind: 'abstain' })

    await waitFor(() => expect(result.current.view.phase).toBe('empty'))
    expect(h.answer).toHaveBeenCalledWith({ scenario_id: 's', question_id: 'a', by: 'dev', abstain: true })
    expect(result.current.solved).toBe(1)
  })

  it('이미 닫힌 문제(409)는 오류로 멈추지 않고 다음 문제로 넘어간다 — 푼 수는 늘지 않는다', async () => {
    h.next.mockResolvedValueOnce(q('a')).mockResolvedValueOnce(q('b'))
    h.answer.mockRejectedValueOnce(new ApiError(409, '답 제출 실패 (409)'))
    const { result } = render()
    await ready(result)

    submit(result, { kind: 'class', id: 'OK' })

    await waitFor(() => expect(result.current.view).toEqual({ phase: 'ready', question: q('b') }))
    await waitFor(() => expect(result.current.busy).toBe(false))
    expect(result.current.solved).toBe(0)
    expect(result.current.answerError).toBeNull()
  })

  it('답 제출이 실패하면(네트워크) 문제는 그대로 남고 이유를 보이고 다시 누를 수 있다', async () => {
    h.next.mockResolvedValue(q('a'))
    h.answer.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    const { result } = render()
    await ready(result)

    submit(result, { kind: 'class', id: 'OK' })

    await waitFor(() => expect(result.current.answerError).toContain('연결'))
    expect(result.current.view).toEqual({ phase: 'ready', question: q('a') })
    expect(result.current.solved).toBe(0)
    await waitFor(() => expect(result.current.busy).toBe(false))

    submit(result, { kind: 'class', id: 'OK' })
    await waitFor(() => expect(result.current.solved).toBe(1))
    expect(result.current.answerError).toBeNull()
  })

  it('답을 보내는 동안에는 두 번째 답을 보내지 않는다', async () => {
    h.next.mockResolvedValue(q('a'))
    let release!: () => void
    h.answer.mockReturnValue(new Promise<QuestionAnswer>((resolve) => { release = () => resolve({ question_id: 'a', recorded: 'label' }) }))
    const { result } = render()
    await ready(result)

    submit(result, { kind: 'class', id: 'OK' })
    await waitFor(() => expect(result.current.busy).toBe(true))
    submit(result, { kind: 'class', id: 'NG' })
    expect(h.answer).toHaveBeenCalledTimes(1)

    act(() => release())
    await waitFor(() => expect(result.current.busy).toBe(false))
  })
})
