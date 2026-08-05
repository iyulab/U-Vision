import { afterEach, describe, expect, it, vi } from 'vitest'

import { ApiError, DetectionUnavailableError, getAdminStatus, inspectImage, listAuditSample, putAudit, setupAdmin } from './api'

function mockFetch(status: number, body: unknown) {
  return vi.fn().mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
    text: async () => JSON.stringify(body),
  } as Response)
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('inspectImage', () => {
  it('503 detection_unavailable → DetectionUnavailableError(+mlHint)', async () => {
    vi.stubGlobal('fetch', mockFetch(503, {
      detection_unavailable: true,
      reason: 'vlm_unavailable',
      ml_hint: { label: 'ng', confidence: 0.8 },
    }))

    const err = await inspectImage(new Blob(), 's', 'd', 'l').catch((e) => e)
    expect(err).toBeInstanceOf(DetectionUnavailableError)
    expect(err.reason).toBe('vlm_unavailable')
    expect(err.mlHint).toEqual({ label: 'ng', confidence: 0.8 })
  })

  it('200 → InspectResult 반환', async () => {
    vi.stubGlobal('fetch', mockFetch(200, {
      verdict: 'OK', findings: '', confidence: 0.9, timestamp: 't', image_id: 'img_1',
    }))

    const result = await inspectImage(new Blob(), 's', 'd', 'l')
    expect(result.verdict).toBe('OK')
  })
})

describe('audit api', () => {
  it('putAudit → {status, prior_label}', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true, status: 200,
      json: async () => ({ status: 'conflicted', prior_label: 'OK' }),
      text: async () => '',
    } as Response))

    const out = await putAudit('s', '2026-06-09', 'img_a', 'NG', 'dev')
    expect(out.status).toBe('conflicted')
    expect(out.prior_label).toBe('OK')
  })

  it('listAuditSample → image_id 배열', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true, status: 200,
      json: async () => ['img_a', 'img_b'],
      text: async () => '',
    } as Response))

    expect(await listAuditSample('s', '2026-06-09')).toEqual(['img_a', 'img_b'])
  })
})

describe('admin setup api', () => {
  it('getAdminStatus → setup_required 반영', async () => {
    vi.stubGlobal('fetch', mockFetch(200, { pin_configured: false, setup_required: true }))
    const status = await getAdminStatus()
    expect(status.setup_required).toBe(true)
    expect(status.pin_configured).toBe(false)
  })

  it('setupAdmin 204 → 성공(무예외)', async () => {
    vi.stubGlobal('fetch', mockFetch(204, null))
    await expect(setupAdmin('tok', '9999')).resolves.toBeUndefined()
  })

  it('setupAdmin 401 → ApiError(상태 보존)', async () => {
    vi.stubGlobal('fetch', mockFetch(401, { detail: 'bad token' }))
    const err = await setupAdmin('wrong', '9999').catch((e) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect(err.status).toBe(401)
  })
})
