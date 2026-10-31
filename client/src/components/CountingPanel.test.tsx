import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import type { CountingMetrics } from '../lib/types'
import { CountingPanel } from './CountingPanel'

afterEach(cleanup)

const BASE: CountingMetrics = {
  checked: 10,
  unavailable: 0,
  ng: 3,
  uncertain: 1,
  dataset_checked: 0,
  composition_checked: 0,
  with_missing: 0,
  with_extra: 0,
  missing_by_kind: {},
  extra_by_kind: {},
  ng_rate: 0.3,
  uncertain_rate: 0.1,
  unavailable_rate: 0,
  missing_rate: null,
  extra_rate: null,
}

describe('CountingPanel', () => {
  it('총 개수 기대: NG · 흔들림 · 불능 비율을 보이고 구성 표는 그리지 않는다', () => {
    render(<CountingPanel counting={BASE} />)

    expect(screen.getByText('30%')).toBeTruthy() // NG 비율
    expect(screen.getByText('3/10 NG')).toBeTruthy()
    expect(screen.getByText('1/10 사람 확인 요구')).toBeTruthy()
    expect(screen.queryByRole('table')).toBeNull()
    expect(screen.getByText(/개수가 맞았는지는 재지 않습니다/)).toBeTruthy() // 정답 없는 신호라는 한계를 화면이 말한다
  })

  it('구성 확인: 종류별 빈도 표를 어긋난 건이 많은 종류부터 보인다', () => {
    render(
      <CountingPanel
        counting={{
          ...BASE,
          composition_checked: 6,
          with_missing: 3,
          with_extra: 1,
          missing_by_kind: { scalpel: 3 },
          extra_by_kind: { clamp: 1 },
          missing_rate: 0.5,
          extra_rate: 1 / 6,
        }}
      />,
    )

    const rows = within(screen.getByRole('table')).getAllByRole('row').slice(1) // 머리글 줄 제외
    expect(rows.map((r) => r.textContent)).toEqual(['scalpel30', 'clamp01'])
    expect(screen.getByText(/구성 확인 6건/)).toBeTruthy()
  })

  it('구성 확인인데 어긋난 종류가 한 번도 없으면 표 대신 그렇다고 말한다', () => {
    render(<CountingPanel counting={{ ...BASE, composition_checked: 4, missing_rate: 0, extra_rate: 0 }} />)

    expect(screen.queryByRole('table')).toBeNull()
    expect(screen.getByText('모자라거나 넘친 종류가 한 번도 없었습니다.')).toBeTruthy()
  })

  it('검출기 불능이 있으면 개수를 모르는 채 통과시키지 않았다는 안내가 뜬다', () => {
    render(<CountingPanel counting={{ ...BASE, unavailable: 2, unavailable_rate: 2 / 12 }} />)

    expect(screen.getByText(/통과시키지 않았습니다/)).toBeTruthy()
    expect(screen.getByText('2/12 판정 못 함')).toBeTruthy()
  })

  it('시험 데이터셋 판정이 섞이면 현장 수치가 아니라고 알린다', () => {
    render(<CountingPanel counting={{ ...BASE, dataset_checked: 10 }} />)

    expect(screen.getByText(/현장 촬영 수치가 아닙니다/)).toBeTruthy()
  })
})
