import type { AdminStatus, AuthorityState, DetectionUnavailable, EventsStatus, InspectResult, InspectResultV2, MetricsSummary, MlResult, Reference, Scenario, ScenarioInput, StoredLabel, StoredResult } from './types'
import type { AuthorityStage } from './authority'
import { resolveApiBase } from './runtimeConfig'

const API_BASE = resolveApiBase()

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

/**
 * 판정 불가(fail-closed, 503) — 주 검출원(VLM) 사용 불가(③.5 E2). transient 네트워크 오류(ApiError)와
 * 구분해 운영 화면이 '판정 불가 — 사람 확인 필요'를 띄우게 한다. mlHint 는 advisory 참고 의견(verdict 아님).
 */
export class DetectionUnavailableError extends Error {
  constructor(
    readonly reason: string,
    readonly mlHint?: MlResult,
  ) {
    super('판정 불가 — 사람 확인 필요')
    this.name = 'DetectionUnavailableError'
  }
}

/**
 * 서버에 닿지 못했다(fetch 자체 실패 — 오프라인 · 연결 끊김). HTTP 응답이 온 실패(ApiError · 판정 불가)와 달리
 * 서버가 아무것도 결정하지 않았으므로, 캡처 큐는 이 오류일 때만 항목을 남겨 다시 보낸다.
 */
export class NetworkError extends Error {
  constructor(cause: unknown) {
    super('서버에 연결할 수 없음', { cause })
    this.name = 'NetworkError'
  }
}

async function post(url: string, body: FormData, headers?: HeadersInit): Promise<Response> {
  try {
    return await fetch(url, { method: 'POST', body, headers })
  } catch (e) {
    throw new NetworkError(e)
  }
}

async function ensureOk(res: Response, what: string): Promise<void> {
  if (res.ok) return
  const detail = await res.text().catch(() => '')
  throw new ApiError(res.status, `${what} 실패 (${res.status}) ${detail}`.trim())
}

/**
 * 같은 캡처를 다시 보내도 서버가 한 번만 판정 · 기록하게 하는 키(`Idempotency-Key`) — 캡처 id 를 쓴다. 응답을 잃고
 * 다시 보내면 서버는 처음 응답을 그대로 돌려준다.
 */
function idempotency(captureId?: string): HeadersInit | undefined {
  return captureId ? { 'Idempotency-Key': captureId } : undefined
}

/** 캡처 이미지를 서버로 보내 판정 결과를 받는다. device 식별자는 멀티태블릿 출처 구분용(additive). */
export async function inspectImage(
  image: Blob,
  scenarioId: string,
  deviceId: string,
  deviceLabel: string,
  capturedAt?: string,
  captureId?: string,
): Promise<InspectResult> {
  const form = new FormData()
  form.append('image', image, 'capture.jpg')
  form.append('scenario_id', scenarioId)
  form.append('device_id', deviceId)
  form.append('device_label', deviceLabel)
  if (capturedAt) form.append('captured_at', capturedAt)

  const res = await post(`${API_BASE}/inspect`, form, idempotency(captureId))
  if (res.status === 503) {
    const body = (await res.json().catch(() => null)) as DetectionUnavailable | null
    if (body?.detection_unavailable)
      throw new DetectionUnavailableError(body.reason, body.ml_hint)
  }
  await ensureOk(res, '판정 요청')
  return (await res.json()) as InspectResult
}

/**
 * 결과 v2 로 판정한다(`POST /v2/inspect`) — 오투입 확인처럼 요청 맥락(기대 품번 · 장치가 읽은 코드)이 필요한
 * 시나리오에 쓴다. 판정 불가(503)는 v1 과 같이 DetectionUnavailableError.
 */
export async function inspectImageV2(
  image: Blob,
  scenarioId: string,
  deviceId: string,
  deviceLabel: string,
  context: { expectedPart?: string; observedCode?: string },
  capturedAt?: string,
  captureId?: string,
): Promise<InspectResultV2> {
  const form = new FormData()
  form.append('image', image, 'capture.jpg')
  form.append('scenario_id', scenarioId)
  form.append('device_id', deviceId)
  form.append('device_label', deviceLabel)
  if (context.expectedPart) form.append('expected_part', context.expectedPart)
  if (context.observedCode) form.append('observed_code', context.observedCode)
  if (capturedAt) form.append('captured_at', capturedAt)

  const res = await post(`${API_BASE}/v2/inspect`, form, idempotency(captureId))
  if (res.status === 503) {
    const body = (await res.json().catch(() => null)) as InspectResultV2 | null
    if (body?.detection_unavailable) throw new DetectionUnavailableError(body.reason ?? 'unavailable')
  }
  await ensureOk(res, '판정 요청')
  return (await res.json()) as InspectResultV2
}

// --- 결과 조회 (무인증 읽기 — 서버 /api/results* 계약) --------------------

/** 시나리오의 검사 날짜 목록(yyyy-MM-dd, 최신 먼저). 기록 없으면 빈 배열. */
export async function listResultDates(scenarioId: string): Promise<string[]> {
  const res = await fetch(
    `${API_BASE}/results/dates?scenario_id=${encodeURIComponent(scenarioId)}`,
  )
  await ensureOk(res, '검사 날짜 목록')
  return (await res.json()) as string[]
}

/** 시나리오·날짜의 검사 결과 레코드(image_id 순). */
export async function listResults(scenarioId: string, date: string): Promise<StoredResult[]> {
  const res = await fetch(
    `${API_BASE}/results?scenario_id=${encodeURIComponent(scenarioId)}&date=${encodeURIComponent(date)}`,
  )
  await ensureOk(res, '검사 결과 목록')
  return (await res.json()) as StoredResult[]
}

/** 저장된 캡처 이미지 서빙 URL(무인증, `<img src>` 직접 사용). */
export function resultImageUrl(scenarioId: string, date: string, imageId: string): string {
  return `${API_BASE}/results/image?scenario_id=${encodeURIComponent(scenarioId)}&date=${encodeURIComponent(date)}&image_id=${encodeURIComponent(imageId)}`
}

// --- 사람 라벨 (무인증 운영 데이터 — 서버 /results/label[s] 계약) -----------

/** 시나리오·날짜의 사람 라벨 목록(표 병합용). */
export async function listLabels(scenarioId: string, date: string): Promise<StoredLabel[]> {
  const res = await fetch(
    `${API_BASE}/results/labels?scenario_id=${encodeURIComponent(scenarioId)}&date=${encodeURIComponent(date)}`,
  )
  await ensureOk(res, '라벨 목록')
  return (await res.json()) as StoredLabel[]
}

/** 사람 라벨 쓰기/정정(무인증). label 은 LABEL_SET 멤버. by=라벨러 device id(C1 provenance). */
export async function putLabel(
  scenarioId: string,
  date: string,
  imageId: string,
  label: string,
  by?: string,
): Promise<void> {
  const res = await fetch(`${API_BASE}/results/label`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ scenario_id: scenarioId, date, image_id: imageId, label, by }),
  })
  await ensureOk(res, '라벨 저장')
}

/** 사람 라벨 삭제(무인증, 미라벨로 환원). */
export async function deleteLabel(
  scenarioId: string,
  date: string,
  imageId: string,
): Promise<void> {
  const res = await fetch(
    `${API_BASE}/results/label?scenario_id=${encodeURIComponent(scenarioId)}&date=${encodeURIComponent(date)}&image_id=${encodeURIComponent(imageId)}`,
    { method: 'DELETE' },
  )
  await ensureOk(res, '라벨 삭제')
}

// --- 라벨 감사 (C1 — 블라인드 재라벨, 무인증 운영 데이터) --------------------

/** 블라인드 재감사 표본(image_id 배열 — 직전 라벨 미포함). */
export async function listAuditSample(scenarioId: string, date: string): Promise<string[]> {
  const res = await fetch(
    `${API_BASE}/results/audit-sample?scenario_id=${encodeURIComponent(scenarioId)}&date=${encodeURIComponent(date)}`,
  )
  await ensureOk(res, '감사 표본')
  return (await res.json()) as string[]
}

/** 블라인드 재라벨 제출 → 일관성 결과 + 공개된 직전 라벨(C1). by=라벨러 device id. */
export async function putAudit(
  scenarioId: string,
  date: string,
  imageId: string,
  label: string,
  by?: string,
): Promise<{ status: string; prior_label: string }> {
  const res = await fetch(`${API_BASE}/results/audit`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ scenario_id: scenarioId, date, image_id: imageId, label, by }),
  })
  await ensureOk(res, '감사 제출')
  return (await res.json()) as { status: string; prior_label: string }
}

// --- 메트릭/관측성 (B3, 무인증 읽기 — 서버 /api/metrics 계약) ---------------

/** 시나리오·날짜의 메트릭 집계(agreement·degrade·검토율·NG recall). 데이터 없으면 0 집계. */
export async function getMetrics(scenarioId: string, date: string): Promise<MetricsSummary> {
  const res = await fetch(
    `${API_BASE}/metrics?scenario_id=${encodeURIComponent(scenarioId)}&date=${encodeURIComponent(date)}`,
  )
  await ensureOk(res, '메트릭 조회')
  return (await res.json()) as MetricsSummary
}

/** 판정 사건 내보내기(웹훅) 받는 곳별 대기 · 포기 수(무인증 읽기). */
export async function getEventsStatus(): Promise<EventsStatus> {
  const res = await fetch(`${API_BASE}/events/status`)
  await ensureOk(res, '사건 내보내기 상태')
  return (await res.json()) as EventsStatus
}

// --- 관리자 PIN 최초 설정 (first-run bootstrap — 서버 /admin/* 계약) ----------
// 계정 시스템 없는 셀프호스트에서 신규 설치가 서버 파일 수정·재시작 없이 PIN 을 정하게 한다.
// status 는 무인증, setup 은 서버 데이터 폴더의 일회용 setup-token(파일시스템 = 신뢰 루트)으로 게이트.

/** PIN 설정 상태(무인증). setup_required 면 콘솔이 최초 설정 화면을 띄운다. */
export async function getAdminStatus(): Promise<AdminStatus> {
  const res = await fetch(`${API_BASE}/admin/status`)
  await ensureOk(res, '관리자 상태 조회')
  return (await res.json()) as AdminStatus
}

/** 최초 PIN 설정(토큰 게이트). 204 성공, 401=토큰 불일치·400=PIN 형식·409=이미 설정·410=비활성. */
export async function setupAdmin(setupToken: string, newPin: string): Promise<void> {
  const res = await fetch(`${API_BASE}/admin/setup`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ setup_token: setupToken, new_pin: newPin }),
  })
  await ensureOk(res, '최초 PIN 설정')
}

const PIN_HEADER = 'X-Admin-Pin'

// --- 권한 이양 사다리 (A1 — get 무인증·promote/demote PIN) --------------------

/** 시나리오의 현재 권한 단계 + 이력(무인증). 부재면 서버가 advisory 합성. */
export async function getAuthority(scenarioId: string): Promise<AuthorityState> {
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(scenarioId)}/authority`)
  await ensureOk(res, '권한 단계 조회')
  return (await res.json()) as AuthorityState
}

/** 단계 격상(PIN, 단조 상향). */
export async function promoteAuthority(
  scenarioId: string,
  stage: AuthorityStage,
  pin: string,
  by?: string,
  reason?: string,
): Promise<void> {
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(scenarioId)}/authority/promote`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', [PIN_HEADER]: pin },
    body: JSON.stringify({ stage, by, reason }),
  })
  await ensureOk(res, '단계 격상')
}

/** 단계 격하(PIN, 1단계 하향). */
export async function demoteAuthority(
  scenarioId: string,
  pin: string,
  by?: string,
  reason?: string,
): Promise<void> {
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(scenarioId)}/authority/demote`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', [PIN_HEADER]: pin },
    body: JSON.stringify({ by, reason }),
  })
  await ensureOk(res, '단계 격하')
}

// --- 시나리오 CRUD (S-B 서버 계약) ---------------------------------------
// 읽기(목록)는 무인증, 변경(생성/수정/삭제)은 관리자 PIN 헤더(X-Admin-Pin).

/** 모든 시나리오 정의(무인증). */
export async function listScenarios(): Promise<Scenario[]> {
  const res = await fetch(`${API_BASE}/scenarios`)
  await ensureOk(res, '시나리오 목록')
  return (await res.json()) as Scenario[]
}

/** 시나리오 생성(PIN). 201 → 확정 id 포함 시나리오. */
export async function createScenario(input: ScenarioInput, pin: string): Promise<Scenario> {
  const res = await fetch(`${API_BASE}/scenarios`, {
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
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', [PIN_HEADER]: pin },
    body: JSON.stringify(input),
  })
  await ensureOk(res, '시나리오 수정')
  return (await res.json()) as Scenario
}

/** 시나리오 삭제(PIN). */
export async function deleteScenario(id: string, pin: string): Promise<void> {
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: { [PIN_HEADER]: pin },
  })
  await ensureOk(res, '시나리오 삭제')
}

// --- 기준 이미지 (S-D 서버 계약) -----------------------------------------

/** 기준 이미지 서빙 URL(무인증, 미리보기용). */
export function referenceUrl(scenarioId: string, label: 'ok' | 'ng', refId: string): string {
  return `${API_BASE}/scenarios/${encodeURIComponent(scenarioId)}/references/${label}/${encodeURIComponent(refId)}`
}

/** 시나리오의 기준 이미지 목록(무인증). */
export async function listReferences(scenarioId: string): Promise<Reference[]> {
  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(scenarioId)}/references`)
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

  const res = await fetch(`${API_BASE}/scenarios/${encodeURIComponent(scenarioId)}/references`, {
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
