import { afterEach, describe, expect, it, vi } from 'vitest'

import { DetectionUnavailableError, inspectImageV2 } from './api'
import { readCode } from './barcode'
import { isVerification, shortVerdictLabel, termOf, toInspectResult, verdictLabel } from './verification'

const ponyfillDetect = vi.hoisted(() => vi.fn())
vi.mock('./barcodePonyfill', () => ({
  BarcodeDetector: class {
    detect = ponyfillDetect
  },
}))

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('verification', () => {
  it('유형이 verification 일 때만 오투입 확인', () => {
    expect(isVerification({ type: 'verification' })).toBe(true)
    expect(isVerification({ type: 'inspection' })).toBe(false)
    expect(isVerification({})).toBe(false)
  })

  it('결과 v2 → 운영 결과: 불일치는 NG 쪽, 일치는 OK 쪽, 클래스는 표시용으로 보존', () => {
    const base = { decision_id: 'img_1', scenario_id: 's', timestamp: 't', confidence: 0.8 }
    const mismatch = toInspectResult({ ...base, class: 'MISMATCH', findings: '다른 부품' })
    expect(mismatch).toMatchObject({ verdict: 'NG', class_label: 'MISMATCH', image_id: 'img_1', findings: '다른 부품' })
    expect(toInspectResult({ ...base, class: 'MATCH' })).toMatchObject({ verdict: 'OK', findings: '' })
    // 불량 유형은 NG 쪽 — 운영 화면이 OK 로 내보내면 안 된다.
    expect(toInspectResult({ ...base, class: 'scratch' })).toMatchObject({ verdict: 'NG', class_label: 'scratch' })
    expect(toInspectResult({ ...base, class: 'OK' })).toMatchObject({ verdict: 'OK' })
  })

  it('대조 단이 잡은 불일치를 위 단이 뒤집으면 차단 자세 · 충돌을 그대로 싣는다', () => {
    const held = toInspectResult({
      decision_id: 'img_1',
      scenario_id: 'part',
      timestamp: 'TS',
      class: 'MATCH',
      requires_review: true,
      posture: 'review_block',
      contested_by: [{ tier: 'match', class: 'MISMATCH' }],
    })
    expect(held).toMatchObject({ verdict: 'OK', requires_review: true, posture: 'review_block' })
    expect(held.contested_by).toEqual([{ tier: 'match', class: 'MISMATCH' }])
  })

  it('배지 문구 — 오투입은 일치/불일치, 검사는 OK/NG', () => {
    expect(verdictLabel({ verdict: 'OK', class_label: 'MATCH' })).toBe('✅ 일치')
    expect(verdictLabel({ verdict: 'NG', class_label: 'MISMATCH' })).toBe('❌ 불일치')
    expect(verdictLabel({ verdict: 'NG' })).toBe('❌ NG')
    expect(verdictLabel({ verdict: 'OK' })).toBe('✅ OK')
    expect(shortVerdictLabel({ verdict: 'NG', class_label: 'MISMATCH' })).toBe('불일치')
    expect(shortVerdictLabel({ verdict: 'OK' })).toBe('OK')
  })
})

describe('termOf', () => {
  it('오투입 확인은 저장값 OK/NG 를 일치/불일치로, 검사는 그대로', () => {
    expect(termOf('OK', 'verification')).toBe('일치')
    expect(termOf('NG', 'verification')).toBe('불일치')
    expect(termOf('NG', 'inspection')).toBe('NG')
    expect(termOf('OK', undefined)).toBe('OK')
  })
})

describe('inspectImageV2', () => {
  function stubFetch(status: number, body: unknown) {
    const fetch = vi.fn().mockResolvedValue({
      ok: status >= 200 && status < 300,
      status,
      json: async () => body,
      text: async () => JSON.stringify(body),
    } as Response)
    vi.stubGlobal('fetch', fetch)
    return fetch
  }

  it('기대 품번 · 읽은 코드를 폼에 싣고 /v2/inspect 로 보낸다', async () => {
    const fetch = stubFetch(200, { decision_id: 'img_1', scenario_id: 's', timestamp: 't', class: 'MATCH' })

    const result = await inspectImageV2(new Blob(), 's', 'd', 'l', { expectedPart: 'P-100', observedCode: 'P-100' })

    expect(result.class).toBe('MATCH')
    const [url, init] = fetch.mock.calls[0]
    expect(String(url)).toMatch(/\/v2\/inspect$/)
    const form = init.body as FormData
    expect(form.get('expected_part')).toBe('P-100')
    expect(form.get('observed_code')).toBe('P-100')
  })

  it('캡처 id 를 Idempotency-Key 헤더로 보낸다', async () => {
    const fetch = stubFetch(200, { decision_id: 'img_1', scenario_id: 's', timestamp: 't', class: 'MATCH' })
    await inspectImageV2(new Blob(), 's', 'd', 'l', { expectedPart: 'P-100' }, undefined, 'cap-1')
    expect(fetch.mock.calls[0][1].headers).toEqual({ 'Idempotency-Key': 'cap-1' })
  })

  it('읽은 코드가 없으면 그 필드를 보내지 않는다', async () => {
    const fetch = stubFetch(200, { decision_id: 'img_1', scenario_id: 's', timestamp: 't', class: 'MATCH' })
    await inspectImageV2(new Blob(), 's', 'd', 'l', { expectedPart: 'P-100' })
    expect((fetch.mock.calls[0][1].body as FormData).has('observed_code')).toBe(false)
  })

  it('503 → DetectionUnavailableError', async () => {
    stubFetch(503, { decision_id: 'dec_1', scenario_id: 's', timestamp: 't', detection_unavailable: true, reason: 'vlm_unavailable' })
    const err = await inspectImageV2(new Blob(), 's', 'd', 'l', { expectedPart: 'P-100' }).catch((e) => e)
    expect(err).toBeInstanceOf(DetectionUnavailableError)
    expect(err.reason).toBe('vlm_unavailable')
  })
})

describe('readCode', () => {
  it('내장 BarcodeDetector 가 없으면 WASM 구현으로 읽는다', async () => {
    vi.stubGlobal('BarcodeDetector', undefined)
    vi.stubGlobal('createImageBitmap', vi.fn().mockResolvedValue({ close: vi.fn() }))
    ponyfillDetect.mockResolvedValue([{ rawValue: 'P-200' }])
    expect(await readCode(new Blob())).toBe('P-200')
  })

  it('WASM 구현을 불러오지 못하면 undefined(코드 없이 보낸다)', async () => {
    vi.stubGlobal('BarcodeDetector', undefined)
    vi.stubGlobal('createImageBitmap', vi.fn().mockResolvedValue({ close: vi.fn() }))
    ponyfillDetect.mockRejectedValue(new Error('wasm fetch failed'))
    expect(await readCode(new Blob())).toBeUndefined()
  })

  it('읽은 첫 비어 있지 않은 값을 다듬어 돌려준다', async () => {
    vi.stubGlobal('createImageBitmap', vi.fn().mockResolvedValue({ close: vi.fn() }))
    vi.stubGlobal('BarcodeDetector', class {
      detect = vi.fn().mockResolvedValue([{ rawValue: ' ' }, { rawValue: ' P-100 ' }])
    })
    expect(await readCode(new Blob())).toBe('P-100')
  })

  it('디코드 실패는 undefined', async () => {
    vi.stubGlobal('createImageBitmap', vi.fn().mockRejectedValue(new Error('bad image')))
    vi.stubGlobal('BarcodeDetector', class {
      detect = vi.fn()
    })
    expect(await readCode(new Blob())).toBeUndefined()
  })
})
