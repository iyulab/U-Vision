import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { Scenario } from '../lib/types'

const getAuthority = vi.fn()
const getMetrics = vi.fn()
const promoteAuthority = vi.fn()

vi.mock('../lib/api', () => ({
  getAuthority: (id: string) => getAuthority(id),
  getMetrics: (id: string, date: string, days: number) => getMetrics(id, date, days),
  promoteAuthority: (...args: unknown[]) => promoteAuthority(...args),
  demoteAuthority: vi.fn(),
}))

import { AuthorityPanel } from './AuthorityPanel'

afterEach(() => {
  cleanup()
  getAuthority.mockReset()
  getMetrics.mockReset()
  promoteAuthority.mockReset()
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

  // 근거 없는 격상(D-116) — 서버도 확인 없이는 409 로 거절하므로 콘솔은 미리 «알고 하는가» 를 묻고, 확인했을 때만 확인 표지를 보낸다.
  const runWithPin = async (action: (pin: string) => Promise<void>) => {
    await action('1234')
    return 'ok' as const
  }

  it('신호가 «적격» 이 아니면 격상 단추가 바로 보내지 않고 확인을 묻는다 — 확인하면 확인 표지와 함께 보낸다', async () => {
    getAuthority.mockResolvedValue({ stage: 'advisory', history: [] })
    getMetrics.mockResolvedValue({ promotion_eligible: null })
    promoteAuthority.mockResolvedValue(undefined)
    render(<AuthorityPanel scenarios={scenarios} initialScenarioId="photo" runWithPin={runWithPin} />)

    fireEvent.click(await screen.findByText(/로 격상/))
    expect(await screen.findByTestId('acknowledge-box')).toBeTruthy()
    expect(promoteAuthority).not.toHaveBeenCalled()

    fireEvent.click(screen.getByText('근거 없이 격상 확정'))
    await waitFor(() => expect(promoteAuthority).toHaveBeenCalledTimes(1))
    expect(promoteAuthority).toHaveBeenCalledWith('photo', 'co_primary', '1234', 'console', undefined, true)
  })

  it('취소하면 아무것도 보내지 않는다', async () => {
    getAuthority.mockResolvedValue({ stage: 'advisory', history: [] })
    getMetrics.mockResolvedValue({ promotion_eligible: false })
    render(<AuthorityPanel scenarios={scenarios} initialScenarioId="photo" runWithPin={runWithPin} />)

    fireEvent.click(await screen.findByText(/로 격상/))
    fireEvent.click(await screen.findByText('취소'))
    expect(screen.queryByTestId('acknowledge-box')).toBeNull()
    expect(promoteAuthority).not.toHaveBeenCalled()
  })

  it('신호가 «적격» 이면 확인 없이 바로 보낸다(확인 표지 없음)', async () => {
    getAuthority.mockResolvedValue({ stage: 'advisory', history: [] })
    getMetrics.mockResolvedValue({ promotion_eligible: true })
    promoteAuthority.mockResolvedValue(undefined)
    render(<AuthorityPanel scenarios={scenarios} initialScenarioId="photo" runWithPin={runWithPin} />)

    await screen.findByTestId('promotion-evidence')
    fireEvent.click(screen.getByText(/로 격상/))
    await waitFor(() => expect(promoteAuthority).toHaveBeenCalledTimes(1))
    expect(promoteAuthority).toHaveBeenCalledWith('photo', 'co_primary', '1234', 'console')
    expect(screen.queryByTestId('acknowledge-box')).toBeNull()
  })

  it('전이 이력에서 확인하고 올린 격상에만 «근거 없이 올림» 표지를 보인다', async () => {
    getAuthority.mockResolvedValue({
      stage: 'co_primary',
      history: [
        { from: 'shadow', to: 'advisory', at: '2026-11-06T01:00:00Z', by: 'console', mode: 'promote', reason: null },
        { from: 'advisory', to: 'co_primary', at: '2026-11-06T02:00:00Z', by: 'console', mode: 'promote', reason: null,
          acknowledged_unverified: true, unverified_basis: ['signal_insufficient'] },
      ],
    })
    getMetrics.mockResolvedValue({})
    render(<AuthorityPanel scenarios={scenarios} initialScenarioId="photo" runWithPin={vi.fn()} />)

    await screen.findByText(/전이 이력/)
    const marks = screen.getAllByTestId('unverified-mark')
    expect(marks).toHaveLength(1)
    expect(marks[0].textContent).toContain('표본 부족')
  })
})
