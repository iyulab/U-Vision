/**
 * 캡처에서 바코드 · QR 을 읽는다 — 브라우저 내장 `BarcodeDetector`(Android Chrome 등)를 먼저 쓰고, 없으면 같은 API 의
 * ZXing WASM 구현(`barcodePonyfill`, iOS Safari 등)을 그때 불러온다. 못 읽으면 undefined: 서버의 대조 단이
 * 기권하고 VLM 이 기대 품번을 들고 판정한다.
 */

interface DetectedBarcode {
  rawValue: string
}

interface BarcodeDetectorLike {
  detect(image: ImageBitmapSource): Promise<DetectedBarcode[]>
}

type BarcodeDetectorCtor = new () => BarcodeDetectorLike

async function detectorCtor(): Promise<BarcodeDetectorCtor> {
  const native = (globalThis as { BarcodeDetector?: BarcodeDetectorCtor }).BarcodeDetector
  if (native) return native
  return (await import('./barcodePonyfill')).BarcodeDetector
}

export async function readCode(image: Blob): Promise<string | undefined> {
  try {
    const Detector = await detectorCtor()
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
