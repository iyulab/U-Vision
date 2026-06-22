/**
 * 태블릿(디바이스) 식별 — localStorage 영속. 한 시나리오를 여러 태블릿이 촬영할 때
 * 결과 출처를 구분하기 위함. id는 안정 UUID(내부 고유성), label은 운영자 표시용(예 "라인 A 입구").
 * 라이브러리 schema가 아니라 앱 자체 wire에 실린다(additive).
 */
const ID_KEY = 'uvision.deviceId'
const LABEL_KEY = 'uvision.deviceLabel'

/** 안정 디바이스 UUID. 최초 호출 시 생성·영속. localStorage 불가 시 ""(식별 불가 graceful). */
export function getDeviceId(): string {
  try {
    let id = localStorage.getItem(ID_KEY)
    if (!id) {
      id = crypto.randomUUID()
      localStorage.setItem(ID_KEY, id)
    }
    return id
  } catch {
    return ''
  }
}

/** 운영자 지정 라벨. 미설정/불가 시 "". */
export function getDeviceLabel(): string {
  try {
    return localStorage.getItem(LABEL_KEY) ?? ''
  } catch {
    return ''
  }
}

export function setDeviceLabel(label: string): void {
  try {
    localStorage.setItem(LABEL_KEY, label)
  } catch {
    /* noop — 세션 내 값은 호출부 state 유지 */
  }
}
