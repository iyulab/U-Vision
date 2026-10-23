import { describe, expect, it } from 'vitest'
import { derivedScenarioId, isValidScenarioId, needsExplicitScenarioId } from './scenarioId'

describe('scenarioId', () => {
  it('이름에서 서버와 같은 id 바탕을 만든다', () => {
    expect(derivedScenarioId('PCB Top Inspect')).toBe('pcb-top-inspect')
    expect(derivedScenarioId('라인3 오투입 확인')).toBe('3')
    expect(derivedScenarioId('상면 검사')).toBe('scenario')
    expect(derivedScenarioId('  Line A  ')).toBe('line-a')
  })

  it('영문이 남지 않는 이름이면 id 를 직접 정하라고 권한다', () => {
    expect(needsExplicitScenarioId('라인3 오투입 확인')).toBe(true)
    expect(needsExplicitScenarioId('상면 검사')).toBe(true)
    expect(needsExplicitScenarioId('PCB 상면 검사')).toBe(false)
    expect(needsExplicitScenarioId('')).toBe(false)
  })

  it('서버가 받는 글자만 통과', () => {
    expect(isValidScenarioId('line3-misload')).toBe(true)
    expect(isValidScenarioId('A.b_c-1')).toBe(true)
    for (const bad of ['', '라인3', 'a b', '../x', '..', '.', 'x'.repeat(129)]) expect(isValidScenarioId(bad)).toBe(false)
  })
})
