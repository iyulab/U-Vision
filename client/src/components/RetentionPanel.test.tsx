import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { RetentionPurgeInput, RetentionPurgeResponse, Scenario } from '../lib/types'

const runRetention = vi.fn<(id: string, input: RetentionPurgeInput, pin: string) => Promise<RetentionPurgeResponse>>()

vi.mock('../lib/api', () => {
  class ApiError extends Error {
    constructor(
      readonly status: number,
      message: string,
    ) {
      super(message)
    }
  }
  return {
    ApiError,
    runRetention: (id: string, input: RetentionPurgeInput, pin: string) => runRetention(id, input, pin),
    getHealth: () => Promise.reject(new Error('볼륨 없음')), // DataVolumePanel 은 아무것도 그리지 않는다
  }
})

import { ApiError } from '../lib/api'
import { RetentionPanel } from './RetentionPanel'

const scenarios = [{ scenario_id: 's1', name: '크로메이트' }] as unknown as Scenario[]

function planResponse(over: Partial<RetentionPurgeResponse> = {}): RetentionPurgeResponse {
  return {
    scenario_id: 's1',
    from: '2026-01-01',
    to: '2026-01-31',
    dry_run: true,
    purged: false,
    plan_fingerprint: 'fp-1',
    kinds: [{ kind: 'records', days: 31, files: 1200, bytes: 1024 }],
    lost: { results: 600, human_labels: 40, labels_not_exported: 5, open_questions: 2, decisions: 0, metric_rows: 0 },
    covered_by_export: false,
    ...over,
  }
}

/** PIN 을 이미 받은 것처럼 액션을 바로 실행하는 러너. */
const runWithPin = vi.fn(async (action: (pin: string) => Promise<void>) => {
  await action('1234')
  return 'ok' as const
})

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date('2026-10-31T12:00:00Z'))
})

afterEach(() => {
  cleanup()
  runRetention.mockReset()
  runWithPin.mockClear()
  vi.useRealTimers()
})

function fillForm() {
  fireEvent.change(screen.getByLabelText(/시작일/), { target: { value: '2026-01-01' } })
  fireEvent.change(screen.getByLabelText(/종료일/), { target: { value: '2026-01-31' } })
  fireEvent.click(screen.getByLabelText(/판정 기록/))
}

function renderPanel() {
  return render(<RetentionPanel scenarios={scenarios} initialScenarioId="s1" runWithPin={runWithPin} />)
}

describe('RetentionPanel', () => {
  it('입력이 비어 있으면 계획 받기를 막는다', () => {
    renderPanel()
    expect((screen.getByText(/계획 받기/) as HTMLButtonElement).disabled).toBe(true)
  })

  it('오늘 이후 종료일은 안내하고 막는다', () => {
    renderPanel()
    fireEvent.change(screen.getByLabelText(/시작일/), { target: { value: '2026-10-01' } })
    fireEvent.change(screen.getByLabelText(/종료일/), { target: { value: '2026-10-31' } })
    fireEvent.click(screen.getByLabelText(/판정 기록/))
    expect(screen.getByText(/오늘\(UTC\)과 그 뒤는 지울 수 없습니다/)).toBeTruthy()
    expect((screen.getByText(/계획 받기/) as HTMLButtonElement).disabled).toBe(true)
  })

  it('계획을 받으면 잃는 것을 보이되 아직 지우지 않는다', async () => {
    runRetention.mockResolvedValue(planResponse())
    renderPanel()
    fillForm()
    fireEvent.click(screen.getByText(/계획 받기/))

    expect(await screen.findByText(/지우면 잃는 것/)).toBeTruthy()
    expect(screen.getByText(/판정 레코드 600건/)).toBeTruthy()
    expect(screen.getByText(/5건은 어느 데이터셋 export 에도 담기지 않았습니다/)).toBeTruthy()
    expect(screen.getByText(/먼저 export 하기를 권합니다/)).toBeTruthy()
    expect(runRetention).toHaveBeenCalledTimes(1)
    expect(runRetention.mock.calls[0][1]).toMatchObject({ from: '2026-01-01', to: '2026-01-31', kinds: ['records'], dry_run: true })
  })

  it('확인 체크 전에는 지울 수 없고, 체크하면 계획의 지문을 되보낸다', async () => {
    runRetention.mockResolvedValueOnce(planResponse())
    renderPanel()
    fillForm()
    fireEvent.click(screen.getByText(/계획 받기/))
    await screen.findByText(/지우면 잃는 것/)

    const purgeButton = screen.getByText('지우기') as HTMLButtonElement
    expect(purgeButton.disabled).toBe(true)

    runRetention.mockResolvedValueOnce(planResponse({ dry_run: false, purged: true }))
    fireEvent.click(screen.getByLabelText(/되돌릴 수 없음을 이해했고/))
    expect((screen.getByText('지우기') as HTMLButtonElement).disabled).toBe(false)
    fireEvent.click(screen.getByText('지우기'))

    expect(await screen.findByText('지웠습니다.')).toBeTruthy()
    expect(runRetention).toHaveBeenCalledTimes(2)
    expect(runRetention.mock.calls[1][1]).toMatchObject({ dry_run: false, plan_fingerprint: 'fp-1' })
    // 지운 뒤에는 계획 단추도 확인도 남지 않는다 — 같은 계획으로 두 번 지우지 않는다.
    expect(screen.queryByText('지우기')).toBeNull()
  })

  it('계획을 받은 뒤 입력을 바꾸면 그 계획으로는 지울 수 없다', async () => {
    runRetention.mockResolvedValue(planResponse())
    renderPanel()
    fillForm()
    fireEvent.click(screen.getByText(/계획 받기/))
    await screen.findByText(/지우면 잃는 것/)

    fireEvent.change(screen.getByLabelText(/종료일/), { target: { value: '2026-02-10' } })
    expect(screen.queryByText(/지우면 잃는 것/)).toBeNull()
    expect(screen.queryByText('지우기')).toBeNull()
  })

  it('서버가 409 로 거절하면 계획을 버리고 다시 받게 한다', async () => {
    runRetention.mockResolvedValueOnce(planResponse())
    renderPanel()
    fillForm()
    fireEvent.click(screen.getByText(/계획 받기/))
    await screen.findByText(/지우면 잃는 것/)

    runRetention.mockRejectedValueOnce(new ApiError(409, '보존 삭제 실패 (409) 계획이 달라졌다'))
    fireEvent.click(screen.getByLabelText(/되돌릴 수 없음을 이해했고/))
    fireEvent.click(screen.getByText('지우기'))

    await waitFor(() => expect(screen.getByText(/구간 안이 달라졌습니다/)).toBeTruthy())
    expect(screen.queryByText('지우기')).toBeNull()
    expect(screen.queryByText('지웠습니다.')).toBeNull()
  })

  it('지울 파일이 없는 계획에는 지우기 단추를 내지 않는다', async () => {
    runRetention.mockResolvedValue(
      planResponse({
        kinds: [{ kind: 'records', days: 0, files: 0, bytes: 0 }],
        lost: { results: 0, human_labels: 0, labels_not_exported: 0, open_questions: 0, decisions: 0, metric_rows: 0 },
        covered_by_export: true,
      }),
    )
    renderPanel()
    fillForm()
    fireEvent.click(screen.getByText(/계획 받기/))

    expect(await screen.findByText(/지울 것이 없습니다/)).toBeTruthy()
    expect(screen.queryByText('지우기')).toBeNull()
  })

  it('PIN 이 틀리면 안내하고 아무것도 받지 않는다', async () => {
    const unauthorized = vi.fn(async () => 'unauthorized' as const)
    render(<RetentionPanel scenarios={scenarios} initialScenarioId="s1" runWithPin={unauthorized} />)
    fillForm()
    fireEvent.click(screen.getByText(/계획 받기/))
    expect(await screen.findByText('PIN 이 올바르지 않습니다.')).toBeTruthy()
    expect(screen.queryByText(/지우면 잃는 것/)).toBeNull()
  })
})
