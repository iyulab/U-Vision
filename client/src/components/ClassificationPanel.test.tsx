import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import type { ClassificationMetrics } from '../lib/types'
import { ClassificationPanel } from './ClassificationPanel'

afterEach(cleanup)

const names: Record<string, string> = { A100: '품번 A', reject: '불량' }
const nameOf = (v: string) => names[v] ?? v

const BASE: ClassificationMetrics = {
  checked: 40,
  predicted: { A100: 25, B200: 10, reject: 5 },
  labeled: 35,
  agreed: 30,
  confusion: { A100: { A100: 20, B200: 2 }, B200: { B200: 8 }, reject: { A100: 3, reject: 2 } },
  agreement_rate: 30 / 35,
}

describe('ClassificationPanel', () => {
  it('판정 · 라벨 · 일치율 셀과 판정 분포를 표시 이름으로 보인다', () => {
    render(<ClassificationPanel classification={BASE} declared={['A100', 'B200', 'reject']} nameOf={nameOf} />)
    expect(screen.getByText('40')).toBeTruthy()
    expect(screen.getByText('86%')).toBeTruthy() // 30/35
    expect(screen.getByText('30/35 일치')).toBeTruthy()
    expect(screen.getByTestId('predicted-distribution').textContent).toBe('판정 분포 — 품번 A 25 · B200 10 · 불량 5')
  })

  it('혼동 행렬은 선언한 순서로 정답 행 · 판정 열이고 재현율 열이 붙는다', () => {
    render(<ClassificationPanel classification={BASE} declared={['A100', 'B200', 'reject']} nameOf={nameOf} />)
    const table = screen.getByTestId('confusion-table')
    const rows = within(table)
      .getAllByRole('row')
      .map((r) => [...r.querySelectorAll('th,td')].map((c) => c.textContent))
    expect(rows[0].slice(1)).toEqual(['품번 A', 'B200', '불량', '재현율'])
    expect(rows[1]).toEqual(['품번 A', '20', '2', '0', '91%'])
    expect(rows[3]).toEqual(['불량', '3', '0', '2', '40%'])
  })

  it('사람 라벨이 없으면 일치율을 0% 로 위장하지 않고 안내하며 행렬은 그리지 않는다', () => {
    render(
      <ClassificationPanel
        classification={{ checked: 12, predicted: { A100: 12 }, labeled: 0, agreed: 0, confusion: {} }}
        declared={['A100']}
        nameOf={nameOf}
      />,
    )
    expect(screen.getAllByText('—').length).toBeGreaterThan(0)
    expect(screen.getByText(/사람 라벨이 아직 없어/)).toBeTruthy()
    expect(screen.queryByTestId('confusion-table')).toBeNull()
  })

  it('라벨이 적으면 일치율은 참고만 하라는 경고를 보인다', () => {
    render(
      <ClassificationPanel
        classification={{
          checked: 12,
          predicted: { A100: 12 },
          labeled: 6,
          agreed: 5,
          confusion: { A100: { A100: 5, B200: 1 } },
          agreement_rate: 5 / 6,
        }}
        declared={['A100', 'B200']}
        nameOf={nameOf}
      />,
    )
    expect(screen.getByText(/6건뿐/)).toBeTruthy()
  })
})
