/**
 * video 의 현재 프레임을 JPEG Blob 으로 캡처한다.
 *
 * ROI crop 은 아직 적용하지 않는다(전체 프레임 전송). ROI 한정 캡처는
 * 토큰 절약 차원의 개선 후보로 carry(로드맵 P2/cycle-05 참조).
 */
export async function captureFrame(video: HTMLVideoElement, quality = 0.9): Promise<Blob> {
  const w = video.videoWidth
  const h = video.videoHeight
  if (!w || !h) throw new Error('비디오 프레임이 아직 준비되지 않음')

  const canvas = document.createElement('canvas')
  canvas.width = w
  canvas.height = h
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('캔버스 2D 컨텍스트 획득 실패')
  ctx.drawImage(video, 0, 0, w, h)

  return await new Promise<Blob>((resolve, reject) => {
    canvas.toBlob(
      (blob) => (blob ? resolve(blob) : reject(new Error('JPEG 인코딩 실패'))),
      'image/jpeg',
      quality,
    )
  })
}
