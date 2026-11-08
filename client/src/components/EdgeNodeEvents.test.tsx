import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { PinRunResult } from '../hooks/usePin'
import type { EdgeNodeEvents } from '../lib/types'

const listEdgeNodeEvents = vi.fn<(id: string, pin: string, limit?: number) => Promise<EdgeNodeEvents>>()

vi.mock('../lib/api', () => ({
  listEdgeNodeEvents: (id: string, pin: string, limit?: number) => listEdgeNodeEvents(id, pin, limit),
}))

import { EdgeNodeEventsSection, POLL_MS } from './EdgeNodeEvents'

const T = '2026-11-08T03:00:00+00:00'

function view(over: Partial<EdgeNodeEvents['current']> = {}, events: EdgeNodeEvents['events'] = []): EdgeNodeEvents {
  return { node_id: 'n1', events, scanned: events.length, truncated: false, current: { counts: [], alarms: [], sources: [], ...over } }
}

/** 열기 — 첫 읽기는 0 ms 타이머로 예약되므로 타이머를 한 번 밀어 준다. */
async function openPanel() {
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: '센 값 · 사건 보기' }))
    await vi.advanceTimersByTimeAsync(0)
  })
}

const runWithPin = vi.fn<(action: (pin: string) => Promise<void>) => Promise<PinRunResult>>()

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  runWithPin.mockImplementation(async (action) => {
    await action('1234')
    return 'ok'
  })
})

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  listEdgeNodeEvents.mockReset()
  runWithPin.mockReset()
})

describe('EdgeNodeEventsSection', () => {
  it('does not read (or ask for a PIN) until it is opened', () => {
    render(<EdgeNodeEventsSection nodeId="n1" runWithPin={runWithPin} />)
    expect(runWithPin).not.toHaveBeenCalled()
    expect(listEdgeNodeEvents).not.toHaveBeenCalled()
  })

  it('shows the current counts, the source state and the events newest first', async () => {
    listEdgeNodeEvents.mockResolvedValue(
      view(
        {
          counts: [{ scenario_id: 's', rule: 'stock', source: 'dock', zone: 'z1', class: 'box', count: 6, since: T, device_time: T, received_at: T }],
          sources: [{ scenario_id: 's', source: 'dock', camera: 'cam-1', state: 'ok', device_time: T, received_at: T }],
        },
        [
          { node_id: 'n1', event_id: 'e2', seq: 2, scenario_id: 's', kind: 'count_changed', device_time: T, received_at: T, data: { rule: 'stock', previous: 4, count: 6 } },
          { node_id: 'n1', event_id: 'e1', seq: 1, scenario_id: 's', kind: 'count_changed', device_time: T, received_at: T, data: { rule: 'stock', previous: null, count: 4 } },
        ],
      ),
    )
    render(<EdgeNodeEventsSection nodeId="n1" runWithPin={runWithPin} />)

    await openPanel()

    expect(listEdgeNodeEvents).toHaveBeenCalledWith('n1', '1234', 50)
    expect(within(await screen.findByTestId('edge-current-counts')).getByText(/stock · 구역 z1 · box: 6개/)).toBeTruthy()
    expect(within(screen.getByTestId('edge-current-sources')).getByText(/cam-1: 정상/)).toBeTruthy()
    const lines = within(screen.getByTestId('edge-events')).getAllByRole('listitem')
    expect(lines[0].textContent).toContain('개수 4 → 6')
    expect(lines[1].textContent).toContain('개수 처음 → 4')
  })

  it('marks the last count stale when the source lost its signal, instead of leaving it looking current', async () => {
    listEdgeNodeEvents.mockResolvedValue(
      view({
        counts: [{ scenario_id: 's', rule: 'stock', source: 'dock', zone: 'z1', class: 'box', count: 6, since: T, device_time: T, received_at: T }],
        sources: [{ scenario_id: 's', source: 'dock', camera: 'cam-1', state: 'no_signal', device_time: T, received_at: T }],
      }),
    )
    render(<EdgeNodeEventsSection nodeId="n1" runWithPin={runWithPin} />)
    await openPanel()

    const count = within(await screen.findByTestId('edge-current-counts')).getByRole('listitem')
    expect(count.getAttribute('data-stale')).toBe('true')
    expect(count.textContent).toContain('낡은 값')
    expect(within(screen.getByTestId('edge-current-sources')).getByText(/cam-1: 신호 없음/)).toBeTruthy()
  })

  it('says so when the node has sent nothing yet', async () => {
    listEdgeNodeEvents.mockResolvedValue(view())
    render(<EdgeNodeEventsSection nodeId="n1" runWithPin={runWithPin} />)
    await openPanel()
    expect(await screen.findByText('아직 이 노드에서 온 사건이 없습니다.')).toBeTruthy()
  })

  it('reads again on a timer while open, and stops when closed', async () => {
    listEdgeNodeEvents.mockResolvedValue(view())
    render(<EdgeNodeEventsSection nodeId="n1" runWithPin={runWithPin} />)
    await openPanel()
    expect(listEdgeNodeEvents).toHaveBeenCalledTimes(1)

    await act(async () => {
      await vi.advanceTimersByTimeAsync(POLL_MS)
    })
    expect(listEdgeNodeEvents).toHaveBeenCalledTimes(2)

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '센 값 · 사건 닫기' }))
    })
    await act(async () => {
      await vi.advanceTimersByTimeAsync(POLL_MS * 3)
    })
    expect(listEdgeNodeEvents).toHaveBeenCalledTimes(2)
  })

  it('closes instead of asking for the PIN again when the PIN was cancelled', async () => {
    runWithPin.mockResolvedValue('cancelled')
    render(<EdgeNodeEventsSection nodeId="n1" runWithPin={runWithPin} />)
    await openPanel()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(POLL_MS * 3)
    })

    expect(runWithPin).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: '센 값 · 사건 보기' })).toBeTruthy()
  })

  it('shows a failed read as text and keeps the panel open', async () => {
    listEdgeNodeEvents.mockRejectedValue(new Error('노드 사건 읽기 실패'))
    render(<EdgeNodeEventsSection nodeId="n1" runWithPin={runWithPin} />)
    await openPanel()
    expect(await screen.findByText('노드 사건 읽기 실패')).toBeTruthy()
    expect(screen.getByRole('button', { name: '센 값 · 사건 닫기' })).toBeTruthy()
  })
})
