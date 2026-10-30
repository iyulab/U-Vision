import { describe, expect, it } from 'vitest'

import { ApiError } from './api'
import { answerPayload, choiceTone, isClosedQuestion, questionErrorText } from './question'

describe('answerPayload', () => {
  it('클래스를 고르면 choice 만 싣고 abstain 은 싣지 않는다', () => {
    expect(answerPayload('s', 'q1', { kind: 'class', id: 'NG' }, 'dev')).toEqual({
      scenario_id: 's', question_id: 'q1', by: 'dev', choice: 'NG',
    })
  })

  it('모르겠음은 abstain 만 싣고 choice 는 싣지 않는다 — 서버는 정확히 하나만 받는다', () => {
    const body = answerPayload('s', 'q1', { kind: 'abstain' }, 'dev')
    expect(body).toEqual({ scenario_id: 's', question_id: 'q1', by: 'dev', abstain: true })
    expect('choice' in body).toBe(false)
  })
})

describe('isClosedQuestion', () => {
  it('409(이미 닫힘) · 404(사라짐)는 다음 문제로 넘어갈 일이다', () => {
    expect(isClosedQuestion(new ApiError(409, 'x'))).toBe(true)
    expect(isClosedQuestion(new ApiError(404, 'x'))).toBe(true)
  })

  it('그 밖의 오류 · 네트워크 실패는 문제를 그대로 두고 다시 누르게 한다', () => {
    expect(isClosedQuestion(new ApiError(500, 'x'))).toBe(false)
    expect(isClosedQuestion(new ApiError(400, 'x'))).toBe(false)
    expect(isClosedQuestion(new TypeError('Failed to fetch'))).toBe(false)
  })
})

describe('questionErrorText', () => {
  it('서버 응답 본문을 작업자에게 보이지 않는다', () => {
    const text = questionErrorText(new ApiError(500, '답 제출 실패 (500) System.Exception: secret detail'))
    expect(text).not.toContain('secret')
    expect(text).toContain('서버')
  })

  it('네트워크 실패는 연결 안내, 400 은 지원하지 않는 시나리오 안내', () => {
    expect(questionErrorText(new TypeError('Failed to fetch'))).toContain('연결')
    expect(questionErrorText(new ApiError(400, 'x'))).toContain('시나리오')
  })
})

describe('choiceTone', () => {
  it('정상(OK)은 ok, 그 밖의 클래스는 defect — 대소문자를 무시한다', () => {
    expect(choiceTone('OK')).toBe('ok')
    expect(choiceTone('ok')).toBe('ok')
    expect(choiceTone('NG')).toBe('defect')
    expect(choiceTone('scratch')).toBe('defect')
  })
})
