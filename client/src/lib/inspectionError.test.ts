import { describe, expect, it } from 'vitest'

import { DetectionUnavailableError } from './api'
import { classifyTriggerError } from './inspectionError'

describe('classifyTriggerError', () => {
  it('DetectionUnavailableError → unavailable phase + hint, error null', () => {
    const s = classifyTriggerError(
      new DetectionUnavailableError('vlm_unavailable', { label: 'ng', confidence: 0.8 }),
    )
    expect(s.phase).toBe('unavailable')
    expect(s.unavailable).toEqual({ reason: 'vlm_unavailable', mlHint: { label: 'ng', confidence: 0.8 } })
    expect(s.error).toBeNull()
  })

  it('일반 Error → error phase, unavailable null', () => {
    const s = classifyTriggerError(new Error('network'))
    expect(s.phase).toBe('error')
    expect(s.error).toBe('network')
    expect(s.unavailable).toBeNull()
  })

  it('비-Error throw → error phase, 기본 메시지', () => {
    const s = classifyTriggerError('oops')
    expect(s.phase).toBe('error')
    expect(s.error).toBe('판정 실패')
  })
})
