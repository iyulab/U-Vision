import type { InspectResult, Reference, Scenario, ScenarioInput } from './types'

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? ''

/** API 오류 — HTTP 상태를 보존해 호출부가 401(PIN 불일치) 등을 구분할 수 있게 한다. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

async function ensureOk(res: Response, what: string): Promise<void> {
  if (res.ok) return
  const detail = await res.text().catch(() => '')
  throw new ApiError(res.status, `${what} 실패 (${res.status}) ${detail}`.trim())
}

/** 캡처 이미지를 서버로 보내 판정 결과를 받는다. */
export async function inspectImage(image: Blob, scenarioId: string): Promise<InspectResult> {
  const form = new FormData()
  form.append('image', image, 'capture.jpg')
  form.append('scenario_id', scenarioId)

  const res = await fetch(`${API_BASE}/api/inspect`, { method: 'POST', body: form })
  await ensureOk(res, '판정 요청')
  return (await res.json()) as InspectResult
}

// --- 시나리오 CRUD (S-B 서버 계약) ---------------------------------------
// 읽기(목록)는 무인증, 변경(생성/수정/삭제)은 관리자 PIN 헤더(X-Admin-Pin).

const PIN_HEADER = 'X-Admin-Pin'

/** 모든 시나리오 정의(무인증). */
export async function listScenarios(): Promise<Scenario[]> {
  const res = await fetch(`${API_BASE}/api/scenarios`)
  await ensureOk(res, '시나리오 목록')
  return (await res.json()) as Scenario[]
}

/** 시나리오 생성(PIN). 201 → 확정 id 포함 시나리오. */
export async function createScenario(input: ScenarioInput, pin: string): Promise<Scenario> {
  const res = await fetch(`${API_BASE}/api/scenarios`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', [PIN_HEADER]: pin },
    body: JSON.stringify(input),
  })
  await ensureOk(res, '시나리오 생성')
  return (await res.json()) as Scenario
}

/** 시나리오 수정(PIN, id 불변). */
export async function updateScenario(
  id: string,
  input: ScenarioInput,
  pin: string,
): Promise<Scenario> {
  const res = await fetch(`${API_BASE}/api/scenarios/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', [PIN_HEADER]: pin },
    body: JSON.stringify(input),
  })
  await ensureOk(res, '시나리오 수정')
  return (await res.json()) as Scenario
}

/** 시나리오 삭제(PIN). */
export async function deleteScenario(id: string, pin: string): Promise<void> {
  const res = await fetch(`${API_BASE}/api/scenarios/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: { [PIN_HEADER]: pin },
  })
  await ensureOk(res, '시나리오 삭제')
}

// --- 기준 이미지 (S-D 서버 계약) -----------------------------------------

/** 기준 이미지 서빙 URL(무인증, 미리보기용). */
export function referenceUrl(scenarioId: string, label: 'ok' | 'ng', refId: string): string {
  return `${API_BASE}/api/scenarios/${encodeURIComponent(scenarioId)}/references/${label}/${encodeURIComponent(refId)}`
}

/** 시나리오의 기준 이미지 목록(무인증). */
export async function listReferences(scenarioId: string): Promise<Reference[]> {
  const res = await fetch(`${API_BASE}/api/scenarios/${encodeURIComponent(scenarioId)}/references`)
  await ensureOk(res, '기준 이미지 목록')
  return (await res.json()) as Reference[]
}

/** 기준 이미지 업로드(PIN). NG 면 ngLabel 로 불량 유형 지정. */
export async function uploadReference(
  scenarioId: string,
  file: File,
  label: 'ok' | 'ng',
  ngLabel: string | undefined,
  pin: string,
): Promise<Reference> {
  const form = new FormData()
  form.append('image', file)
  form.append('label', label)
  if (label === 'ng' && ngLabel) form.append('ng_label', ngLabel)

  const res = await fetch(`${API_BASE}/api/scenarios/${encodeURIComponent(scenarioId)}/references`, {
    method: 'POST',
    headers: { [PIN_HEADER]: pin },
    body: form,
  })
  await ensureOk(res, '기준 이미지 업로드')
  return (await res.json()) as Reference
}

/** 기준 이미지 삭제(PIN). */
export async function deleteReference(
  scenarioId: string,
  label: 'ok' | 'ng',
  refId: string,
  pin: string,
): Promise<void> {
  const res = await fetch(referenceUrl(scenarioId, label, refId), {
    method: 'DELETE',
    headers: { [PIN_HEADER]: pin },
  })
  await ensureOk(res, '기준 이미지 삭제')
}
