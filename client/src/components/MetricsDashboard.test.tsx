import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { CountingMetrics, MetricsSummary, Scenario } from '../lib/types'

const listMetricsDates = vi.fn<(id: string) => Promise<string[]>>()
const getMetrics = vi.fn<(id: string, date: string) => Promise<MetricsSummary>>()

// 검사 날짜 목록(listResultDates)은 일부러 내보내지 않는다 — 대시보드가 그것을 다시 부르면 시험이 깨져 되돌림을 알린다.
vi.mock('../lib/api', () => ({
  listMetricsDates: (id: string) => listMetricsDates(id),
  getMetrics: (id: string, date: string) => getMetrics(id, date),
  getMemory: () => Promise.reject(new Error('기억 꺼짐')),
  getTrainingReadiness: () => Promise.reject(new Error('준비 없음')),
  getSolverTrust: () => Promise.reject(new Error('심은 문제 없음')),
  getAmbiguity: () => Promise.reject(new Error('닫힌 문제 없음')),
}))

import { MetricsDashboard } from './MetricsDashboard'

const scenarios = [{ scenario_id: 's1', name: '계수', type: 'counting' }] as unknown as Scenario[]

const DOWN_ALL_DAY: CountingMetrics = {
  checked: 0,
  unavailable: 6,
  ng: 0,
  uncertain: 0,
  dataset_checked: 0,
  composition_checked: 0,
  with_missing: 0,
  with_extra: 0,
  missing_by_kind: {},
  extra_by_kind: {},
  ng_rate: null,
  uncertain_rate: null,
  unavailable_rate: 1,
  missing_rate: null,
  extra_rate: null,
}

beforeEach(() => {
  listMetricsDates.mockReset()
  getMetrics.mockReset()
})
afterEach(cleanup)

describe('MetricsDashboard', () => {
  it('날짜는 메트릭이 있는 날에서 고른다 — 레코드가 없는 검출기 불능의 날도 읽힌다', async () => {
    listMetricsDates.mockResolvedValue(['2026-10-30'])
    getMetrics.mockResolvedValue({ scenario_id: 's1', date: '2026-10-30', counting: DOWN_ALL_DAY } as unknown as MetricsSummary)

    render(<MetricsDashboard scenarios={scenarios} initialScenarioId="s1" />)

    await waitFor(() => expect(getMetrics).toHaveBeenCalledWith('s1', '2026-10-30'))
    expect(await screen.findByText('6/6 판정 못 함')).toBeTruthy()
    expect(listMetricsDates).toHaveBeenCalledWith('s1')
  })

  it('메트릭이 있는 날이 없으면 날짜 선택이 꺼지고 메트릭을 묻지 않는다', async () => {
    listMetricsDates.mockResolvedValue([])

    render(<MetricsDashboard scenarios={scenarios} initialScenarioId="s1" />)

    await waitFor(() => expect(listMetricsDates).toHaveBeenCalled())
    expect((screen.getByLabelText('날짜') as HTMLSelectElement).disabled).toBe(true)
    expect(getMetrics).not.toHaveBeenCalled()
  })
})
