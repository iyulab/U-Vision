import { describe, expect, it } from 'vitest'

import { laplacianVariance } from './sharpness'

/** w×h RGBA 버퍼를 픽셀 그레이값 함수로 생성. */
function makeGray(w: number, h: number, valueAt: (x: number, y: number) => number): Uint8ClampedArray {
  const buf = new Uint8ClampedArray(w * h * 4)
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const p = (y * w + x) * 4
      const v = valueAt(x, y)
      buf[p] = buf[p + 1] = buf[p + 2] = v
      buf[p + 3] = 255
    }
  }
  return buf
}

describe('laplacianVariance', () => {
  it('균일 이미지(흐림)는 분산 ~0', () => {
    const flat = makeGray(16, 16, () => 128)
    expect(laplacianVariance(flat, 16, 16)).toBeCloseTo(0, 6)
  })

  it('체커보드(선명한 엣지)는 큰 분산', () => {
    const checker = makeGray(16, 16, (x, y) => ((x + y) % 2 === 0 ? 0 : 255))
    expect(laplacianVariance(checker, 16, 16)).toBeGreaterThan(1000)
  })

  it('선명한 이미지가 흐린 이미지보다 분산이 크다', () => {
    const sharp = makeGray(16, 16, (x) => (x % 2 === 0 ? 0 : 255))
    const blurry = makeGray(16, 16, (x) => 100 + x) // 완만한 그라데이션
    expect(laplacianVariance(sharp, 16, 16)).toBeGreaterThan(
      laplacianVariance(blurry, 16, 16),
    )
  })

  it('3px 미만은 0(계산 불가)', () => {
    expect(laplacianVariance(new Uint8ClampedArray(2 * 2 * 4), 2, 2)).toBe(0)
  })
})
