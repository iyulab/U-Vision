import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { DecisionEvidence, RejudgeResponse } from '../lib/types'
import type { RunWithPin } from '../hooks/usePin'

const getDecisionEvidence = vi.fn<(s: string, d: string, i: string) => Promise<DecisionEvidence | null>>()
const rejudgeResult = vi.fn<(s: string, d: string, i: string, t: string[], pin: string) => Promise<RejudgeResponse>>()
vi.mock('../lib/api', () => ({
  getDecisionEvidence: (s: string, d: string, i: string) => getDecisionEvidence(s, d, i),
  rejudgeResult: (s: string, d: string, i: string, t: string[], pin: string) => rejudgeResult(s, d, i, t, pin),
}))

import { EvidencePanel } from './EvidencePanel'

afterEach(() => {
  cleanup()
  getDecisionEvidence.mockReset()
  rejudgeResult.mockReset()
})

const BASE: DecisionEvidence = {
  image_id: 'img_1',
  scenario_id: 'demo',
  date: '2026-11-04',
  class: 'OK',
  confidence: 0.93,
  ledger_recorded: true,
  settled_by: { tier: 'vlm', kind: 'vlm' },
  tier_version: 'mock:gpt-4o|p:81276d0e|r:none',
  path: [
    { tier: 'ml', kind: 'ml', outcome: 'shadowed' },
    { tier: 'vlm', kind: 'vlm', outcome: 'settled' },
  ],
  ml: { label: 'ng', confidence: 0.71 },
  agreement: false,
  annotations: { 'operative:source': 'vlm', 'operative:stage': 'advisory' },
}

function open() {
  render(<EvidencePanel scenarioId="demo" date="2026-11-04" imageId="img_1" />)
  fireEvent.click(screen.getByRole('button', { name: /판정 근거/ }))
}

describe('EvidencePanel', () => {
  it('펼치기 전에는 서버를 부르지 않는다', () => {
    render(<EvidencePanel scenarioId="demo" date="2026-11-04" imageId="img_1" />)
    expect(getDecisionEvidence).not.toHaveBeenCalled()
  })

  it('펼치면 확정한 단 · 단별 경로 · ML 답을 보인다', async () => {
    getDecisionEvidence.mockResolvedValue(BASE)
    open()
    await waitFor(() => expect(screen.getByText(/VLM\(vlm 단\)이 확정/)).toBeTruthy())
    expect(getDecisionEvidence).toHaveBeenCalledWith('demo', '2026-11-04', 'img_1')
    expect(screen.getByText('답했으나 확정 권한 없음')).toBeTruthy()
    expect(screen.getByText('확정')).toBeTruthy()
    // 아래 단의 답은 레코드의 ML 결과 — VLM 과 다르면 «불일치» 로 드러난다.
    expect(screen.getByText(/ng/)).toBeTruthy()
    expect(screen.getByText(/불일치/)).toBeTruthy()
    expect(screen.getByText('operative:source')).toBeTruthy()
  })

  it('장부가 없으면 경로를 지어내지 않고 사유를 말한다', async () => {
    getDecisionEvidence.mockResolvedValue({ ...BASE, ledger_recorded: false, settled_by: undefined, path: undefined, annotations: undefined, tier_version: undefined })
    open()
    await waitFor(() => expect(screen.getByText(/결정 장부에 이 판정의 기록이 없어/)).toBeTruthy())
    expect(screen.queryByText('확정')).toBeNull()
  })

  it('레코드가 없으면(null) 그렇게 말한다', async () => {
    getDecisionEvidence.mockResolvedValue(null)
    open()
    await waitFor(() => expect(screen.getByText('판정 기록을 찾지 못했습니다')).toBeTruthy())
  })

  it('읽기 실패는 오류로 보인다', async () => {
    getDecisionEvidence.mockRejectedValue(new Error('판정 근거: 500'))
    open()
    await waitFor(() => expect(screen.getByText('판정 근거: 500')).toBeTruthy())
  })

  describe('사람 라벨 · 오라클 · 지난 재판정', () => {
    it('있으면 보인다', async () => {
      getDecisionEvidence.mockResolvedValue({
        ...BASE,
        human_label: 'NG',
        oracle_opinion: 'OK',
        rejudges: [
          {
            image_id: 'img_1', scenario_id: 'demo', date: '2026-11-04', requested_at: '2026-11-04T05:00:00Z',
            original_class: 'OK',
            opinions: [{ tier: 'oracle', kind: 'vlm', status: 'answered', class: 'NG', confidence: 0.77, differs_from_original: true }],
            synthesis: { agreement: 'split', answered: 1, differing_tiers: ['oracle'], votes: { OK: 1, NG: 1 } },
          },
        ],
      })
      open()
      await waitFor(() => expect(screen.getByText(/사람이 정한 라벨/)).toBeTruthy())
      expect(screen.getByText(/오라클의 2차 소견/)).toBeTruthy()
      expect(screen.getByText(/더 센 판정자\(오라클\) 이\(가\) 원 판정과 다릅니다/)).toBeTruthy()
    })
  })

  describe('다시 판정', () => {
    const runWithPin: RunWithPin = async (action) => {
      await action('1234')
      return 'ok'
    }
    const RESPONSE: RejudgeResponse = {
      image_id: 'img_1', scenario_id: 'demo', date: '2026-11-04', requested_at: '2026-11-04T05:00:00Z',
      original_class: 'OK',
      opinions: [
        { tier: 'vlm', kind: 'vlm', status: 'answered', class: 'OK', confidence: 0.9, differs_from_original: false },
        { tier: 'ml', kind: 'ml', status: 'abstained', reason: 'ml disabled' },
      ],
      synthesis: { agreement: 'unanimous', answered: 1, differing_tiers: [], votes: { OK: 2 } },
    }

    function openWithPin() {
      render(<EvidencePanel scenarioId="demo" date="2026-11-04" imageId="img_1" runWithPin={runWithPin} />)
      fireEvent.click(screen.getByRole('button', { name: /판정 근거/ }))
    }

    it('PIN 러너가 없으면(작업자 화면) 조작이 없다', async () => {
      getDecisionEvidence.mockResolvedValue(BASE)
      open()
      await waitFor(() => expect(screen.getByText(/VLM\(vlm 단\)이 확정/)).toBeTruthy())
      expect(screen.queryByRole('button', { name: '다시 판정' })).toBeNull()
    })

    it('단을 고르지 않으면 단추가 눌리지 않는다', async () => {
      getDecisionEvidence.mockResolvedValue(BASE)
      openWithPin()
      const button = await screen.findByRole('button', { name: '다시 판정' })
      expect((button as HTMLButtonElement).disabled).toBe(true)
    })

    it('고른 단을 PIN 으로 물어 결과와 요약을 나란히 보인다', async () => {
      getDecisionEvidence.mockResolvedValue(BASE)
      rejudgeResult.mockResolvedValue(RESPONSE)
      openWithPin()
      fireEvent.click(await screen.findByLabelText('VLM 다시'))
      fireEvent.click(screen.getByLabelText('전용 ML'))
      fireEvent.click(screen.getByRole('button', { name: '다시 판정' }))
      await waitFor(() => expect(screen.getByText('답한 1곳 모두 원 판정과 같습니다')).toBeTruthy())
      expect(rejudgeResult).toHaveBeenCalledWith('demo', '2026-11-04', 'img_1', ['vlm', 'ml'], '1234')
      expect(screen.getByText('기권 — ml disabled')).toBeTruthy()
      expect(screen.getByText('OK 2')).toBeTruthy()
    })

    it('서버가 거절하면 그 사유를 보인다', async () => {
      getDecisionEvidence.mockResolvedValue(BASE)
      rejudgeResult.mockRejectedValue(new Error('오라클이 꺼져 있다(UVision:Oracle:Provider)'))
      openWithPin()
      fireEvent.click(await screen.findByLabelText('더 센 판정자(오라클)'))
      fireEvent.click(screen.getByRole('button', { name: '다시 판정' }))
      await waitFor(() => expect(screen.getByText(/오라클이 꺼져 있다/)).toBeTruthy())
    })
  })
})
