import { describe, expect, it } from 'vitest'

import { LABEL_SET, agreementOf, isValidLabel, labelMapOf } from './labels'
import type { StoredLabel } from './types'

describe('labels', () => {
  it('LABEL_SET is binary OK/NG (v1)', () => {
    expect(LABEL_SET).toEqual(['OK', 'NG'])
  })

  it('isValidLabel accepts set members, rejects others', () => {
    expect(isValidLabel('OK')).toBe(true)
    expect(isValidLabel('NG')).toBe(true)
    expect(isValidLabel('MAYBE')).toBe(false)
    expect(isValidLabel('')).toBe(false)
  })

  it('labelMapOf indexes labels by image_id', () => {
    const labels: StoredLabel[] = [
      { image_id: 'a', label: 'OK', timestamp: 't' },
      { image_id: 'b', label: 'NG', timestamp: 't' },
    ]
    const map = labelMapOf(labels)
    expect(map.get('a')?.label).toBe('OK')
    expect(map.get('b')?.label).toBe('NG')
    expect(map.get('c')).toBeUndefined()
  })

  it('agreementOf compares verdict and label generally', () => {
    expect(agreementOf('OK', undefined)).toBe('unlabeled')
    expect(agreementOf('OK', 'OK')).toBe('match')
    expect(agreementOf('OK', 'NG')).toBe('mismatch')
    expect(agreementOf('NG', 'NG')).toBe('match')
  })
})
