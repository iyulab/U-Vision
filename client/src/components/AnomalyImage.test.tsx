import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { AnomalyMap } from '../lib/types'

const getAnomalyMap = vi.fn<(s: string, d: string, i: string) => Promise<AnomalyMap | null>>()
vi.mock('../lib/api', () => ({
  getAnomalyMap: (s: string, d: string, i: string) => getAnomalyMap(s, d, i),
  resultImageUrl: () => 'about:blank',
}))

import { AnomalyImage } from './AnomalyImage'

afterEach(() => {
  cleanup()
  getAnomalyMap.mockReset()
})

const MAP: AnomalyMap = {
  encoder: 'e',
  rows: 2,
  columns: 2,
  distances: [0.1, 0.1, 0.1, 0.4],
  patch_score: 0.4,
  global_score: 0.02,
  bank_size: 200,
}

function loadImage() {
  const img = screen.getByAltText('검사 캡처')
  Object.defineProperty(img, 'naturalWidth', { value: 400, configurable: true })
  Object.defineProperty(img, 'naturalHeight', { value: 200, configurable: true })
  fireEvent.load(img)
}

describe('AnomalyImage', () => {
  it('지도가 없으면 이미지만 — 열지도 단추가 없다', async () => {
    getAnomalyMap.mockResolvedValue(null)
    render(<AnomalyImage scenarioId="s" date="2026-10-27" imageId="img_1" />)
    await waitFor(() => expect(getAnomalyMap).toHaveBeenCalledWith('s', '2026-10-27', 'img_1'))
    expect(screen.getByAltText('검사 캡처')).toBeTruthy()
    expect(screen.queryByRole('button', { name: /열지도/ })).toBeNull()
  })

  it('지도가 있으면 단추를 보이고 켜면 칸을 겹쳐 그린다 — 점수와 상대값 안내 포함', async () => {
    getAnomalyMap.mockResolvedValue(MAP)
    render(<AnomalyImage scenarioId="s" date="2026-10-27" imageId="img_1" />)
    const toggle = await screen.findByRole('button', { name: '열지도 보기' })
    expect(screen.queryByTestId('heat-layer')).toBeNull()   // 켜기 전에는 그리지 않는다
    expect(screen.getByText('0.400')).toBeTruthy()          // 패치 점수

    loadImage()
    fireEvent.click(toggle)

    const layer = await screen.findByTestId('heat-layer')
    expect(layer.children).toHaveLength(4)
    expect(screen.getByText(/이 사진 안의 상대값/)).toBeTruthy()
    expect(screen.getByRole('button', { name: '열지도 끄기' }).getAttribute('aria-pressed')).toBe('true')

    fireEvent.click(screen.getByRole('button', { name: '열지도 끄기' }))
    expect(screen.queryByTestId('heat-layer')).toBeNull()
  })

  it('지도를 읽다 실패해도 이미지는 그대로다', async () => {
    getAnomalyMap.mockRejectedValue(new Error('boom'))
    render(<AnomalyImage scenarioId="s" date="2026-10-27" imageId="img_1" />)
    await waitFor(() => expect(getAnomalyMap).toHaveBeenCalled())
    expect(screen.getByAltText('검사 캡처')).toBeTruthy()
    expect(screen.queryByRole('button', { name: /열지도/ })).toBeNull()
  })
})
