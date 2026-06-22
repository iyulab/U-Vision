import { afterEach, describe, expect, it, vi } from 'vitest'

import { DetectionUnavailableError, inspectImage, listAuditSample, putAudit } from './api'

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
