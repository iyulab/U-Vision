import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { CountInfo } from '../lib/types'

vi.mock('../lib/api', () => ({ resultImageUrl: () => 'about:blank' }))

import { CountImage } from './CountImage'

afterEach(cleanup)

const COUNT: CountInfo = {
  total: 3,
  by_class: { pill: 3 },
  expected: 3,
  tolerance: 0,
  min_confidence: 0.5,
  uncertain: false,
  boxes: [
    { class: 'pill', x: 0.1, y: 0.1, w: 0.2, h: 0.2, confidence: 0.95 },
    { class: 'pill', x: 0.4, y: 0.1, w: 0.2, h: 0.2, confidence: 0.9 },
    { class: 'pill', x: 0.7, y: 0.1, w: 0.2, h: 0.2, confidence: 0.55 },
  ],
}

describe('CountImage', () => {
  it('센 상자를 사진 위에 그리고 문턱 근처만 구분한다', () => {
    render(<CountImage scenarioId="s" date="2026-10-31" imageId="img_1" count={COUNT} />)

    const layer = screen.getByTestId('count-layer')
    const boxes = [...layer.children] as HTMLElement[]
    expect(boxes).toHaveLength(3)
    expect(boxes.map((b) => b.dataset.near)).toEqual(['false', 'false', 'true'])
    expect(boxes[0].style.left).toBe('10%')
    expect(screen.getByText('센 개수 3 (기대 3 ± 0)')).toBeTruthy()
    expect(screen.getByText(/노란 상자 1개는 문턱 근처/)).toBeTruthy()
    expect(screen.queryByText(/흔들리는 판정/)).toBeNull()
  })

  it('세지 않은 근처 상자는 점선으로 따로 그리고 이유를 말한다', () => {
    const count: CountInfo = {
      ...COUNT,
      uncertain: true,
      near_boxes: [{ class: 'pill', x: 0.5, y: 0.5, w: 0.1, h: 0.1, confidence: 0.45 }],
    }
    render(<CountImage scenarioId="s" date="d" imageId="i" count={count} />)

    const layer = screen.getByTestId('count-layer')
    expect([...layer.children]).toHaveLength(4) // 센 3 + 세지 않은 1
    expect(layer.querySelectorAll('[data-uncounted="true"]')).toHaveLength(1)
    expect(screen.getByText(/점선 상자 1개는 세지 않았지만 문턱 바로 아래/)).toBeTruthy()
  })

  it('근처 상자가 없으면 점선도 안내도 없다', () => {
    render(<CountImage scenarioId="s" date="d" imageId="i" count={COUNT} />)

    expect(screen.getByTestId('count-layer').querySelectorAll('[data-uncounted]')).toHaveLength(0)
    expect(screen.queryByText(/점선 상자/)).toBeNull()
  })

  it('흔들리는 판정이면 사람 확인 대상임을 말한다', () => {
    render(<CountImage scenarioId="s" date="d" imageId="i" count={{ ...COUNT, uncertain: true }} />)

    expect(screen.getByText(/흔들리는 판정이라 NG/)).toBeTruthy()
  })

  it('«상자 끄기» 로 사진만 볼 수 있다', () => {
    render(<CountImage scenarioId="s" date="d" imageId="i" count={COUNT} />)

    fireEvent.click(screen.getByRole('button', { name: '상자 끄기' }))

    expect(screen.queryByTestId('count-layer')).toBeNull()
    expect(screen.getByRole('button', { name: '상자 보기' })).toBeTruthy()
  })

  it('상자가 없는 판정(0개)도 그린다 — 근거가 «아무것도 못 찾음»', () => {
    render(<CountImage scenarioId="s" date="d" imageId="i" count={{ ...COUNT, total: 0, by_class: {}, boxes: [] }} />)

    expect(screen.getByTestId('count-layer').children).toHaveLength(0)
    expect(screen.getByText('센 개수 0 (기대 3 ± 0)')).toBeTruthy()
  })
})
