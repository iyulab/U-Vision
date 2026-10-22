/**
 * 내장 `BarcodeDetector` 가 없는 브라우저(iOS Safari · 데스크톱 Chrome 다수)용 같은 API 의 ZXing WASM 구현.
 * `barcode.ts` 가 필요할 때만 동적 import 한다 — 내장이 있는 기기는 이 청크와 WASM 을 받지 않는다.
 *
 * WASM 은 기본값이 CDN 이라 셀프호스트 · 오프라인 현장에서 깨진다. 번들에 실은 사본을 같은 출처에서 받는다
 * (서비스 워커가 함께 precache 한다).
 */
import { BarcodeDetector, prepareZXingModule } from 'barcode-detector/ponyfill'
import readerWasmUrl from 'zxing-wasm/reader/zxing_reader.wasm?url'

prepareZXingModule({
  overrides: {
    locateFile: (path: string, prefix: string) => (path.endsWith('.wasm') ? readerWasmUrl : prefix + path),
  },
})

export { BarcodeDetector }
