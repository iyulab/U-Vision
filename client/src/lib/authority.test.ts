import { describe, expect, it } from 'vitest'
import { AUTHORITY_STAGES, nextStageUp, stageLabel } from './authority'

describe('authority stages', () => {
  it('orders stages', () => {
    expect(AUTHORITY_STAGES).toEqual(['shadow', 'advisory', 'co_primary', 'ml_primary'])
  })
  it('nextStageUp returns the stage above', () => {
    expect(nextStageUp('advisory')).toBe('co_primary')
    expect(nextStageUp('ml_primary')).toBeNull() // 최상위
  })
  it('stageLabel is human-readable korean', () => {
    expect(stageLabel('co_primary')).toContain('동등')
  })
})
