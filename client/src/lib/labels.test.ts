import { describe, expect, it } from 'vitest'

import { agreementOf, decidedClassOf, labelMapOf, labelsOf, latestOracleOpinion, sameClass } from './labels'
import type { StoredLabel } from './types'

describe('labels', () => {
  it('labelsOf: 불량 유형을 선언한 검사는 클래스 id, 그 밖은 OK/NG', () => {
    const classes = [{ id: 'OK', settleable: true }, { id: 'scratch', must_catch: true }, { id: 'dent', must_catch: true }]
    expect(labelsOf({ classes })).toEqual(['OK', 'scratch', 'dent'])
    expect(labelsOf({})).toEqual(['OK', 'NG'])
    expect(labelsOf(undefined)).toEqual(['OK', 'NG'])
    expect(labelsOf({ type: 'verification', classes: [{ id: 'MATCH' }, { id: 'MISMATCH' }] })).toEqual(['OK', 'NG'])
  })

  it('decidedClassOf: 유형이 있으면 class, 없으면 verdict', () => {
    expect(decidedClassOf({ verdict: 'NG', class: 'dent' })).toBe('dent')
    expect(decidedClassOf({ verdict: 'NG' })).toBe('NG')
  })

  it('sameClass ignores case (ML answers lowercase)', () => {
    expect(sameClass('ng', 'NG')).toBe(true)
    expect(sameClass('dent', 'scratch')).toBe(false)
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
    expect(agreementOf('dent', 'scratch')).toBe('mismatch')
  })

  it('latestOracleOpinion extracts latest oracle event, ignores label/audit modes', () => {
    const l: StoredLabel = {
      image_id: 'a',
      label: 'OK',
      timestamp: 't',
      history: [
        { label: 'OK', by: 'dev', at: 't1', mode: 'label' },
        { label: 'NG', by: 'oracle', at: 't2', mode: 'oracle' },
      ],
    }
    expect(latestOracleOpinion(l)).toBe('NG')
  })

  it('latestOracleOpinion is undefined when no oracle event (or no label)', () => {
    expect(latestOracleOpinion(undefined)).toBeUndefined()
    expect(latestOracleOpinion({ image_id: 'a', label: 'NG', timestamp: 't' })).toBeUndefined()
  })
})
