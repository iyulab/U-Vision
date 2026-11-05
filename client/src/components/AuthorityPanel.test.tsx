import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Scenario } from '../lib/types'

const getAuthority = vi.fn()
const getMetrics = vi.fn()

vi.mock('../lib/api', () => ({
  getAuthority: (id: string) => getAuthority(id),
  getMetrics: (id: string, date: string, days: number) => getMetrics(id, date, days),
  promoteAuthority: vi.fn(),
  demoteAuthority: vi.fn(),
}))

import { AuthorityPanel } from './AuthorityPanel'

afterEach(() => {
  cleanup()
  getAuthority.mockReset()
  getMetrics.mockReset()
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
    getMetrics.mockResolvedValue({})
    render(<AuthorityPanel scenarios={scenarios} initialScenarioId="photo" runWithPin={vi.fn()} />)

    expect(await screen.findByText('한 단계 격하')).toBeTruthy()
    expect(screen.queryByTestId('stream-only')).toBeNull()
    expect(getAuthority).toHaveBeenCalledWith('photo')
  })

  it('격상 근거를 최근 30일 창 집계로 읽어 읽기 전용으로 보이고, 표본 부족을 «적격» 으로 읽지 않는다', async () => {
    getAuthority.mockResolvedValue({ stage: 'advisory', history: [] })
    getMetrics.mockResolvedValue({
      labeled_ng: 12, labeled_ok: 80, ml_ng_scored: 10, ml_ng_hits: 9, ml_ng_recall: 0.9,
      ml_ok_scored: 40, ml_false_alarms: 12, ml_false_alarm_rate: 0.3, promotion_eligible: null,
    })
    render(<AuthorityPanel scenarios={scenarios} initialScenarioId="photo" runWithPin={vi.fn()} />)

    const box = await screen.findByTestId('promotion-evidence')
    expect(box.textContent).toContain('12 / 80')
    expect(box.textContent).toContain('9/10 (90.0 %)')
    expect(box.textContent).toContain('12/40 (30.0 %)')
    expect(screen.getByTestId('promotion-signal').textContent).toContain('표본 부족')
    expect(screen.getByTestId('promotion-signal').textContent).toContain('적격이라는 뜻이 아닙니다')
    expect(getMetrics).toHaveBeenCalledWith('photo', expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/), 30)
  })
})
