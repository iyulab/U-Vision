import { afterEach, describe, expect, it, vi } from 'vitest'

import { ApiError, DetectionUnavailableError, getAdminStatus, getAnomalyMap, inspectImage, listAuditSample, putAudit, setupAdmin } from './api'

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

  it('캡처 id 를 Idempotency-Key 헤더로 보낸다(없으면 헤더 없음)', async () => {
    const fetch = mockFetch(200, { verdict: 'OK', findings: '', confidence: 0.9, timestamp: 't', image_id: 'img_1' })
    vi.stubGlobal('fetch', fetch)

    await inspectImage(new Blob(), 's', 'd', 'l', undefined, 'cap-1')
    await inspectImage(new Blob(), 's', 'd', 'l')

    expect(fetch.mock.calls[0][1].headers).toEqual({ 'Idempotency-Key': 'cap-1' })
    expect(fetch.mock.calls[1][1].headers).toBeUndefined()
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

describe('getAnomalyMap', () => {
  it('404 는 오류가 아니라 «지도 없음»(null)', async () => {
    vi.stubGlobal('fetch', mockFetch(404, { detail: '이상 지도 없음' }))
    expect(await getAnomalyMap('s', '2026-10-27', 'img_1')).toBeNull()
  })

  it('200 → 지도, 그 밖의 실패는 던진다', async () => {
    const map = { encoder: 'e', rows: 1, columns: 1, distances: [0.1], patch_score: 0.1, global_score: 0.01, bank_size: 3 }
    vi.stubGlobal('fetch', mockFetch(200, map))
    expect(await getAnomalyMap('s', 'd', 'i')).toEqual(map)

    vi.stubGlobal('fetch', mockFetch(500, { detail: 'x' }))
    await expect(getAnomalyMap('s', 'd', 'i')).rejects.toBeInstanceOf(ApiError)
  })
})
