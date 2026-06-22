/**
 * 촬영 모드(자동/수동) — 디바이스 단위 localStorage. "이 태블릿을 어떻게 쓰나"의 속성이므로
 * 시나리오가 아닌 디바이스에 둔다(activeScenario 동형 패턴). 기본 auto.
 */
export type CaptureMode = 'auto' | 'manual'

const KEY = 'uvision.captureMode'

export function getCaptureMode(): CaptureMode {
  try {
    return localStorage.getItem(KEY) === 'manual' ? 'manual' : 'auto'
  } catch {
    return 'auto'
  }
}

export function setCaptureMode(mode: CaptureMode): void {
  try {
    localStorage.setItem(KEY, mode)
  } catch {
    /* noop — 세션 내 값은 호출부 state 유지 */
  }
}
