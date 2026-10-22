import { describe, expect, it } from 'vitest'

import { classesFor, defectsOf, formatDefects, parseDefects } from './defectClasses'

describe('defectsOf', () => {
  it('검사가 OK 밖에 둔 유형(표시 이름 포함), 이분 판정 · 오투입 확인은 빈 목록', () => {
    expect(defectsOf({ classes: [{ id: 'OK' }, { id: 'scratch', label: '긁힘' }, { id: 'dent' }] })).toEqual([
      { id: 'scratch', label: '긁힘' },
      { id: 'dent' },
    ])
    expect(defectsOf({ classes: [{ id: 'OK' }, { id: 'NG' }] })).toEqual([])
    expect(defectsOf({})).toEqual([])
    expect(defectsOf({ type: 'verification', classes: [{ id: 'MATCH' }, { id: 'MISMATCH' }] })).toEqual([])
  })

  it('NG 하나에 표시 이름을 붙였으면 그 이름을 지키려고 목록에 남긴다', () => {
    expect(defectsOf({ classes: [{ id: 'OK' }, { id: 'NG', label: '불량' }] })).toEqual([{ id: 'NG', label: '불량' }])
  })
})

describe('parseDefects', () => {
  it('쉼표 · 줄바꿈으로 나누고 «id=표시 이름» 을 읽는다 — 표시 이름은 공백을 담는다', () => {
    expect(parseDefects(' scratch=표면 긁힘, dent\ncrack = 균열 ')).toEqual({
      defects: [{ id: 'scratch', label: '표면 긁힘' }, { id: 'dent' }, { id: 'crack', label: '균열' }],
    })
    expect(parseDefects('')).toEqual({ defects: [] })
  })

  it('서버가 거부할 id · 표시 이름 · OK · 겹침을 막는다', () => {
    expect(parseDefects('긁힘')).toHaveProperty('error')
    expect(parseDefects('NG:scratch')).toHaveProperty('error')
    expect(parseDefects('scratch dent')).toHaveProperty('error') // 공백으로는 나누지 않는다
    expect(parseDefects('scratch=')).toHaveProperty('error')
    expect(parseDefects('scratch=a=b')).toHaveProperty('error')
    expect(parseDefects(`scratch=${'가'.repeat(33)}`)).toHaveProperty('error')
    expect(parseDefects('scratch, ok')).toHaveProperty('error')
    expect(parseDefects('dent, Dent')).toHaveProperty('error')
    expect(parseDefects('scratch=긁힘, dent=긁힘')).toHaveProperty('error')
  })

  it('formatDefects 의 역이다', () => {
    const defects = [{ id: 'scratch', label: '표면 긁힘' }, { id: 'dent' }]
    expect(parseDefects(formatDefects(defects))).toEqual({ defects })
  })
})

describe('classesFor', () => {
  it('유형이 없으면 OK/NG, 있으면 OK + 꼭 잡을 유형들(표시 이름 그대로)', () => {
    expect(classesFor([])).toEqual([
      { id: 'OK', settleable: true },
      { id: 'NG', must_catch: true },
    ])
    expect(classesFor([{ id: 'scratch', label: '긁힘' }])).toEqual([
      { id: 'OK', settleable: true },
      { id: 'scratch', label: '긁힘', must_catch: true },
    ])
  })
})
