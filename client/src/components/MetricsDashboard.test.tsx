import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { CountingMetrics, FlywheelReport, MemoryReport, MetricsSummary, Scenario } from '../lib/types'

const listMetricsDates = vi.fn<(id: string) => Promise<string[]>>()
const getMetrics = vi.fn<(id: string, date: string) => Promise<MetricsSummary>>()
const getMemory = vi.fn<(id: string, date: string, days?: number) => Promise<MemoryReport>>()
const getFlywheel = vi.fn<(id: string, date: string, bucket: number) => Promise<FlywheelReport>>()

// 검사 날짜 목록(listResultDates)은 일부러 내보내지 않는다 — 대시보드가 그것을 다시 부르면 시험이 깨져 되돌림을 알린다.
vi.mock('../lib/api', () => ({
  listMetricsDates: (id: string) => listMetricsDates(id),
  getMetrics: (id: string, date: string) => getMetrics(id, date),
  getMemory: (id: string, date: string, days?: number) => getMemory(id, date, days),
  getFlywheel: (id: string, date: string, bucket: number) => getFlywheel(id, date, bucket),
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
  getMemory.mockReset()
  getMemory.mockRejectedValue(new Error('기억 꺼짐'))
  getFlywheel.mockReset()
  getFlywheel.mockRejectedValue(new Error('곡선 없음'))
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

  describe('기억 문턱 추천', () => {
    const inspection = [{ scenario_id: 's1', name: '검사', type: 'inspection' }] as unknown as Scenario[]
    const report = (over: Partial<MemoryReport> = {}): MemoryReport => ({
      scenario_id: 's1',
      date: '2026-11-01',
      enabled: true,
      replayed: 12,
      replay: [
        { max_distance: 0.1, configured: false, answered: 8, teacher_checked: 8, teacher_agreement: 1, labeled: 8, label_agreement: 1, missed: 0, miss_rate: 0, labeled_defects: 6, defect_pass_rate: 0 },
        { max_distance: 0.15, configured: true, answered: 10, teacher_checked: 10, teacher_agreement: 1, labeled: 10, label_agreement: 0.9, missed: 1, miss_rate: 0.1, labeled_defects: 6, defect_pass_rate: 0.17 },
      ],
      recommendation: {
        max_distance: 0.1,
        answered: 8,
        labeled_defects: 6,
        missed: 0,
        defect_pass_upper_95: 0.393,
        sufficient: false,
        target_miss_rate: 0.02,
        defects_needed: 149,
        reason: '문턱 0.1 까지 불량 6장 중 OK 로 지나간 것이 없다.',
      },
      ...over,
    })
    const open = async (onApply?: React.ComponentProps<typeof MetricsDashboard>['onApplyMemoryDistance']) => {
      listMetricsDates.mockResolvedValue(['2026-11-01'])
      getMetrics.mockResolvedValue({ scenario_id: 's1', date: '2026-11-01' } as unknown as MetricsSummary)
      getMemory.mockResolvedValue(report())
      render(<MetricsDashboard scenarios={inspection} initialScenarioId="s1" onApplyMemoryDistance={onApply} />)
      return await screen.findByTestId('memory-recommendation')
    }

    it('증거가 모자라면 «후보» 로 보이고, 적용은 «증거 부족 — 그래도 적용» 으로만 한다', async () => {
      const apply = vi.fn().mockResolvedValue('ok')
      const card = await open(apply)

      expect(card.textContent).toContain('후보 문턱 0.10')
      expect(card.textContent).toContain('불량 라벨 149장')
      fireEvent.click(screen.getByRole('button', { name: '증거 부족 — 그래도 적용' }))

      await waitFor(() => expect(apply).toHaveBeenCalledWith(inspection[0], 0.1))
      expect(await screen.findByText('문턱 0.10 를 이 시나리오에 적용했습니다.')).toBeTruthy()
      expect(getMemory).toHaveBeenCalledTimes(2) // 적용 뒤 기억 칸을 다시 읽는다
    })

    it('적용 핸들러가 없으면 추천만 보이고 단추는 없다', async () => {
      await open(undefined)
      expect(screen.queryByRole('button', { name: /적용/ })).toBeNull()
    })

    it('PIN 이 틀리면 알리고 다시 읽지 않는다', async () => {
      const card = await open(vi.fn().mockResolvedValue('unauthorized'))
      fireEvent.click(screen.getByRole('button', { name: '증거 부족 — 그래도 적용' }))
      expect(await screen.findByText('PIN 이 올바르지 않습니다. 다시 시도하세요.')).toBeTruthy()
      expect(card.isConnected).toBe(true)
      expect(getMemory).toHaveBeenCalledTimes(1)
    })

    it('추천 문턱이 지금 설정값과 같으면 적용 단추가 없다', async () => {
      listMetricsDates.mockResolvedValue(['2026-11-01'])
      getMetrics.mockResolvedValue({ scenario_id: 's1', date: '2026-11-01' } as unknown as MetricsSummary)
      getMemory.mockResolvedValue(report({ recommendation: { ...report().recommendation!, max_distance: 0.15, sufficient: true } }))
      render(<MetricsDashboard scenarios={inspection} initialScenarioId="s1" onApplyMemoryDistance={vi.fn()} />)

      await screen.findByTestId('memory-recommendation')
      expect(screen.queryByRole('button', { name: /적용/ })).toBeNull()
    })

    it('재생 기간을 바꾸면 그 기간으로 다시 읽는다', async () => {
      await open(undefined)
      fireEvent.change(screen.getByLabelText('재생 기간'), { target: { value: '7' } })
      await waitFor(() => expect(getMemory).toHaveBeenLastCalledWith('s1', '2026-11-01', 7))
    })
  })

  describe('플라이휠 곡선', () => {
    const inspection = [{ scenario_id: 's1', name: '검사', type: 'inspection' }] as unknown as Scenario[]
    const flywheel = (labeled: number): FlywheelReport => ({
      scenario_id: 's1',
      date: '2026-11-01',
      days: 30,
      bucket_size: 25,
      memory_max_distance: 0.15,
      labeled,
      points:
        labeled === 0
          ? []
          : [
              { labeled_through: 25, judged: 25, operative_agreement: 0.8, labeled_defects: 0, operative_missed: 0, memory_answered: 2, memory_answered_rate: 0.08, memory_missed: 0 },
              { labeled_through: 50, judged: 25, operative_agreement: 0.84, labeled_defects: 0, operative_missed: 0, memory_answered: 10, memory_answered_rate: 0.4, memory_agreement: 0.9, memory_missed: 0 },
            ],
    })
    const open = () => {
      listMetricsDates.mockResolvedValue(['2026-11-01'])
      getMetrics.mockResolvedValue({ scenario_id: 's1', date: '2026-11-01' } as unknown as MetricsSummary)
      render(<MetricsDashboard scenarios={inspection} initialScenarioId="s1" />)
    }

    it('라벨이 있으면 곡선 칸에 추세 한 줄과 구간 표가 보인다', async () => {
      getFlywheel.mockResolvedValue(flywheel(50))
      open()

      const panel = await screen.findByTestId('flywheel-panel')
      expect(panel.textContent).toContain('사람이 확정한 라벨 50건')
      expect(screen.getByTestId('flywheel-trend').textContent).toContain('기억이 답한 비율은 오릅니다 (8% → 40%)')
      expect(panel.querySelectorAll('tbody tr').length).toBe(2)
      expect(getFlywheel).toHaveBeenCalledWith('s1', '2026-11-01', 25)
    })

    it('라벨이 하나도 없으면 칸이 없다', async () => {
      getFlywheel.mockResolvedValue(flywheel(0))
      open()
      await waitFor(() => expect(getFlywheel).toHaveBeenCalled())
      expect(screen.queryByTestId('flywheel-panel')).toBeNull()
    })

    it('구간 크기를 바꾸면 그 크기로 다시 읽는다', async () => {
      getFlywheel.mockResolvedValue(flywheel(50))
      open()
      await screen.findByTestId('flywheel-panel')
      fireEvent.change(screen.getByLabelText('구간 크기'), { target: { value: '50' } })
      await waitFor(() => expect(getFlywheel).toHaveBeenLastCalledWith('s1', '2026-11-01', 50))
    })
  })
})
