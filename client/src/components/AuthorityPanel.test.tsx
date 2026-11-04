import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Scenario } from '../lib/types'

const getAuthority = vi.fn()

vi.mock('../lib/api', () => ({
  getAuthority: (id: string) => getAuthority(id),
  promoteAuthority: vi.fn(),
  demoteAuthority: vi.fn(),
}))

import { AuthorityPanel } from './AuthorityPanel'

afterEach(() => {
  cleanup()
  getAuthority.mockReset()
})

const scenarios = [
  { scenario_id: 'photo', name: '1라인 외관' },
  { scenario_id: 'watch', name: '창고 감시', type: 'monitoring' },
] as unknown as Scenario[]

describe('AuthorityPanel', () => {
  it('스트림 감시 시나리오는 권한 사다리 대신 이유를 보이고 서버를 부르지 않는다', async () => {
    render(<AuthorityPanel scenarios={scenarios} initialScenarioId="watch" runWithPin={vi.fn()} />)

    expect(await screen.findByTestId('stream-only')).toBeTruthy()
    expect(screen.queryByText('한 단계 격하')).toBeNull()
    expect(getAuthority).not.toHaveBeenCalled()
  })

  it('사진 판정 시나리오는 그대로 단계를 읽는다', async () => {
    getAuthority.mockResolvedValue({ stage: 'advisory', history: [] })
    render(<AuthorityPanel scenarios={scenarios} initialScenarioId="photo" runWithPin={vi.fn()} />)

    expect(await screen.findByText('한 단계 격하')).toBeTruthy()
    expect(screen.queryByTestId('stream-only')).toBeNull()
    expect(getAuthority).toHaveBeenCalledWith('photo')
  })
})
