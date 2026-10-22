import { describe, expect, it } from 'vitest'

import { classesFor, defectsOf, parseDefects } from './defectClasses'

describe('defectsOf', () => {
  it('검사가 OK 밖에 둔 유형, 이분 판정 · 오투입 확인은 빈 목록', () => {
    expect(defectsOf({ classes: [{ id: 'OK' }, { id: 'scratch' }, { id: 'dent' }] })).toEqual(['scratch', 'dent'])
    expect(defectsOf({ classes: [{ id: 'OK' }, { id: 'NG' }] })).toEqual([])
    expect(defectsOf({})).toEqual([])
    expect(defectsOf({ type: 'verification', classes: [{ id: 'MATCH' }, { id: 'MISMATCH' }] })).toEqual([])
  })
})

describe('parseDefects', () => {
  it('쉼표 · 공백으로 나눈다', () => {
    expect(parseDefects(' scratch, dent  crack ')).toEqual({ ids: ['scratch', 'dent', 'crack'] })
    expect(parseDefects('')).toEqual({ ids: [] })
  })

  it('서버가 거부할 이름 · OK · 겹침을 막는다', () => {
    expect(parseDefects('긁힘')).toHaveProperty('error')
    expect(parseDefects('NG:scratch')).toHaveProperty('error')
    expect(parseDefects('scratch, ok')).toHaveProperty('error')
    expect(parseDefects('dent, Dent')).toHaveProperty('error')
  })
})

describe('classesFor', () => {
  it('유형이 없으면 OK/NG, 있으면 OK + 꼭 잡을 유형들', () => {
    expect(classesFor([])).toEqual([
      { id: 'OK', settleable: true },
      { id: 'NG', must_catch: true },
    ])
    expect(classesFor(['scratch'])).toEqual([
      { id: 'OK', settleable: true },
      { id: 'scratch', must_catch: true },
    ])
  })
})
