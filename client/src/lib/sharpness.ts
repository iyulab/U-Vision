/**
 * 선명도 측정 — 라플라시안 분산. worker/DOM 비의존 순수 로직(테스트 가능).
 *
 * 흐린 이미지는 고주파 성분(엣지)이 적어 라플라시안 분산이 낮다. 임계 미만이면 흐림으로
 * 거부해 VLM 호출 전에 걸러낸다(토큰 절약 + 오판정 방지). (cycle-23 — min_sharpness enforce)
 */

/**
 * RGBA 버퍼의 라플라시안 분산을 구한다. 높을수록 선명, 0 에 가까울수록 흐림/균일.
 * grayscale 변환 후 4-이웃 라플라시안을 컨볼브하고 그 분산을 반환한다.
 */
export function laplacianVariance(
  rgba: Uint8ClampedArray,
  width: number,
  height: number,
): number {
  if (width < 3 || height < 3) return 0

  // grayscale (luma).
  const gray = new Float64Array(width * height)
  for (let i = 0, p = 0; i < gray.length; i++, p += 4) {
    gray[i] = 0.299 * rgba[p] + 0.587 * rgba[p + 1] + 0.114 * rgba[p + 2]
  }

  // 라플라시안(내부 픽셀만) + 분산(sum/sumSq).
  let sum = 0
  let sumSq = 0
  let n = 0
  for (let y = 1; y < height - 1; y++) {
    for (let x = 1; x < width - 1; x++) {
      const i = y * width + x
      const lap = gray[i - width] + gray[i + width] + gray[i - 1] + gray[i + 1] - 4 * gray[i]
      sum += lap
      sumSq += lap * lap
      n++
    }
  }
  if (n === 0) return 0
  const mean = sum / n
  return sumSq / n - mean * mean
}
