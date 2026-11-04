import { describe, expect, it } from 'vitest'
import { operableScenarios, resolveActiveScenario } from './activeScenario'
import type { Scenario } from './types'

const scenario = (id: string, type?: string) => ({ scenario_id: id, name: id, type }) as Scenario

describe('operableScenarios', () => {
  it('hides stream-monitoring scenarios from the capture screen but keeps everything else', () => {
    const list = [scenario('a'), scenario('watch', 'monitoring'), scenario('b', 'counting'), scenario('c', 'verification')]
    expect(operableScenarios(list).map((s) => s.scenario_id)).toEqual(['a', 'b', 'c'])
  })

  it('never lets a monitoring scenario become the active one, even if it was stored', () => {
    const list = operableScenarios([scenario('watch', 'monitoring'), scenario('a')])
    expect(resolveActiveScenario(list, 'watch')?.scenario_id).toBe('a')
    expect(resolveActiveScenario(operableScenarios([scenario('watch', 'monitoring')]), 'watch')).toBeNull()
  })
})
