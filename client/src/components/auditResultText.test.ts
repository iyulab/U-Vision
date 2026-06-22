import { describe, expect, it } from 'vitest'
import { auditResultText } from './auditResultText'

describe('auditResultText', () => {
  it('consistent → 일치 문구, ok true', () => {
    expect(auditResultText('consistent')).toEqual({ text: '✓ 일관됨', ok: true })
  })
  it('conflicted → 충돌 문구, ok false', () => {
    expect(auditResultText('conflicted')).toEqual({ text: '⚠ 충돌 — 검토 필요', ok: false })
  })
  it('기타 → 중립', () => {
    expect(auditResultText('whatever').ok).toBe(false)
  })
})
