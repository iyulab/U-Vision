import { describe, expect, it } from 'vitest'
import { unavailableHintText, unavailableReasonText } from './unavailablePanel'

describe('unavailableHintText', () => {
  it('mlHint 있으면 참고 의견 문구', () => {
    expect(unavailableHintText({ label: 'ng', confidence: 0.83 })).toBe('ML 참고 의견: NG (83%)')
  })
  it('mlHint 없으면 null', () => {
    expect(unavailableHintText(undefined)).toBeNull()
  })
})

describe('unavailableReasonText', () => {
  it('서버의 사유 코드를 작업자가 읽는 말로 바꾼다', () => {
    expect(unavailableReasonText('vlm_unavailable')).toContain('연결하지 못했')
    expect(unavailableReasonText('ml_unavailable')).toContain('ML')
  })

  it('이유가 비었거나 모르는 코드여도 빈 화면이 아니다 — 코드는 괄호에 둔다', () => {
    expect(unavailableReasonText(undefined)).toBe('판정원이 답하지 못했습니다.')
    expect(unavailableReasonText('some_new_code')).toBe('판정원이 답하지 못했습니다(some_new_code).')
  })

  it('이미 문장인 사유(계수 시나리오 등)는 그대로 보인다', () => {
    expect(unavailableReasonText('검출기가 꺼져 있다(UVision:Detection:Provider)')).toBe('검출기가 꺼져 있다(UVision:Detection:Provider)')
  })
})
