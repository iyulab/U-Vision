import type { Roi } from './roi'
import { laplacianVariance } from './sharpness'

export interface CaptureResult {
  blob: Blob
  /** 캡처 영역의 라플라시안 분산(선명도). 흐림 거부 판단에 사용. */
  sharpness: number
}

/**
 * video 의 현재 프레임을 JPEG 으로 캡처한다. ROI 가 유효하면 그 영역만 crop 한다
 * (VLM 토큰 절약 + 검사 영역 집중). 선명도(라플라시안 분산)도 함께 계산해 반환한다.
 */
export async function captureFrame(
  video: HTMLVideoElement,
  roi?: Roi,
  quality = 0.9,
): Promise<CaptureResult> {
  const vw = video.videoWidth
  const vh = video.videoHeight
  if (!vw || !vh) throw new Error('비디오 프레임이 아직 준비되지 않음')

  // ROI(상대 0~1) → 소스 픽셀. 폭/높이가 0 이하면 전체 프레임.
  const useRoi = roi && roi.width > 0 && roi.height > 0
  const sx = useRoi ? Math.round(roi.x * vw) : 0
  const sy = useRoi ? Math.round(roi.y * vh) : 0
  const sw = useRoi ? Math.round(roi.width * vw) : vw
  const sh = useRoi ? Math.round(roi.height * vh) : vh

  const canvas = document.createElement('canvas')
  canvas.width = sw
  canvas.height = sh
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('캔버스 2D 컨텍스트 획득 실패')
  ctx.drawImage(video, sx, sy, sw, sh, 0, 0, sw, sh)

  const sharpness = laplacianVariance(ctx.getImageData(0, 0, sw, sh).data, sw, sh)

  const blob = await new Promise<Blob>((resolve, reject) => {
    canvas.toBlob(
      (b) => (b ? resolve(b) : reject(new Error('JPEG 인코딩 실패'))),
      'image/jpeg',
      quality,
    )
  })

  return { blob, sharpness }
}
