import { describe, expect, it } from 'vitest'
import { unavailableHintText } from './unavailablePanel'

describe('unavailableHintText', () => {
  it('mlHint 있으면 참고 의견 문구', () => {
    expect(unavailableHintText({ label: 'ng', confidence: 0.83 })).toBe('ML 참고 의견: NG (83%)')
  })
  it('mlHint 없으면 null', () => {
    expect(unavailableHintText(undefined)).toBeNull()
  })
})
