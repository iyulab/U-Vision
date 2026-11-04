import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { DecisionEvidence } from '../lib/types'

const getDecisionEvidence = vi.fn<(s: string, d: string, i: string) => Promise<DecisionEvidence | null>>()
vi.mock('../lib/api', () => ({
  getDecisionEvidence: (s: string, d: string, i: string) => getDecisionEvidence(s, d, i),
}))

import { EvidencePanel } from './EvidencePanel'

afterEach(() => {
  cleanup()
  getDecisionEvidence.mockReset()
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
})
