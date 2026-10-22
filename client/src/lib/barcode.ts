/**
 * 캡처에서 바코드 · QR 을 읽는다 — 브라우저 내장 `BarcodeDetector` 가 있을 때만(Android Chrome 등). 없거나 못
 * 읽으면 undefined: 서버의 대조 단이 기권하고 VLM 이 기대 품번을 들고 판정한다. 디코더 라이브러리를 들이지 않는다.
 */

interface DetectedBarcode {
  rawValue: string
}

interface BarcodeDetectorLike {
  detect(image: ImageBitmapSource): Promise<DetectedBarcode[]>
}

type BarcodeDetectorCtor = new () => BarcodeDetectorLike

export async function readCode(image: Blob): Promise<string | undefined> {
  const Detector = (globalThis as { BarcodeDetector?: BarcodeDetectorCtor }).BarcodeDetector
  if (!Detector) return undefined
  try {
    const bitmap = await createImageBitmap(image)
    try {
      const codes = await new Detector().detect(bitmap)
      const value = codes.find((c) => c.rawValue.trim() !== '')?.rawValue.trim()
      return value || undefined
    } finally {
      bitmap.close()
    }
  } catch {
    // 디코드 실패는 판정을 막지 않는다 — 코드 없이 보낸다.
    return undefined
  }
}
