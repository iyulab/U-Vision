import { afterEach, describe, expect, it, vi } from 'vitest'

import { DetectionUnavailableError, inspectImageV2 } from './api'
import { readCode } from './barcode'
import { isVerification, shortVerdictLabel, termOf, toInspectResult, verdictLabel, verdictPillText } from './verification'

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

  describe('다중 분류(classes 를 주면)', () => {
    const base = { decision_id: 'img_1', scenario_id: 's', timestamp: 't', confidence: 0.8 }
    const classes = [{ id: 'A100', label: '품번 A' }, { id: 'B200' }, { id: 'reject', label: '불량', must_catch: true }]

    it('OK/NG 보기는 서버가 v2 `verdict` 로 싣는 값 그대로다 — 클래스 이름으로 짐작하지 않는다', () => {
      expect(toInspectResult({ ...base, class: 'A100', verdict: 'OK' }, classes)).toMatchObject({ verdict: 'OK', class_name: '품번 A', class_label: 'A100' })
      expect(toInspectResult({ ...base, class: 'reject', verdict: 'NG' }, classes)).toMatchObject({ verdict: 'NG', class_name: '불량' })
      // 이름이 'OK' 여도 서버가 NG 라고 하면 NG, 이름이 reject 여도 서버가 OK 라고 하면 OK — 이름은 뜻이 없다.
      expect(toInspectResult({ ...base, class: 'OK', verdict: 'NG' }, [{ id: 'OK', must_catch: true }, { id: 'X' }])).toMatchObject({ verdict: 'NG' })
      expect(toInspectResult({ ...base, class: 'reject', verdict: 'OK' }, classes)).toMatchObject({ verdict: 'OK' })
    })

    it('답에 verdict 가 없으면 NG(안전한 쪽 — 차단) — 선언에 없는 클래스여도 이름 풀이는 id 로 돌아간다', () => {
      expect(toInspectResult({ ...base, class: 'A100' }, classes)).toMatchObject({ verdict: 'NG', class_name: '품번 A' })
      expect(toInspectResult({ ...base, class: 'ghost', verdict: 'OK' }, classes)).toMatchObject({ verdict: 'OK', class_name: 'ghost' })
      expect(toInspectResult({ ...base }, classes)).toMatchObject({ verdict: 'NG', class_name: '?' })
    })

    it('대소문자만 다른 답도 선언한 클래스의 표시 이름으로 푼다', () => {
      expect(toInspectResult({ ...base, class: 'REJECT', verdict: 'NG' }, classes)).toMatchObject({ class_name: '불량' })
      expect(toInspectResult({ ...base, class: 'a100', verdict: 'OK' }, classes)).toMatchObject({ class_name: '품번 A' })
    })

    it('배지 · 목록 문구는 클래스 이름 — 아이콘은 OK/NG 보기를 따른다', () => {
      const ok = toInspectResult({ ...base, class: 'A100', verdict: 'OK' }, classes)
      const ng = toInspectResult({ ...base, class: 'reject', verdict: 'NG' }, classes)
      expect(verdictLabel(ok)).toBe('✅ 품번 A')
      expect(verdictLabel(ng)).toBe('❌ 불량')
      expect(shortVerdictLabel(ok)).toBe('품번 A')
    })
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

describe('verdictPillText', () => {
  const nameOf = (v: string) => ({ A100: '품번 A', scratch: '긁힘' })[v] ?? v

  it('검사: OK/NG 가 본문, NG 가 유형으로 나뉘면 유형이 덧붙는다', () => {
    expect(verdictPillText('OK', 'OK', 'inspection', nameOf)).toEqual({ text: 'OK' })
    expect(verdictPillText('NG', 'scratch', 'inspection', nameOf)).toEqual({ text: 'NG', detail: '긁힘' })
    expect(verdictPillText('NG', undefined, undefined, nameOf)).toEqual({ text: 'NG' })
  })

  it('오투입 확인: 일치/불일치', () => {
    expect(verdictPillText('NG', 'MISMATCH', 'verification', nameOf)).toEqual({ text: '불일치', detail: 'MISMATCH' })
  })

  it('다중 분류: 클래스 이름이 본문 — 저장된 OK/NG 는 문구에 나오지 않는다', () => {
    expect(verdictPillText('OK', 'A100', 'classification', nameOf)).toEqual({ text: '품번 A' })
    expect(verdictPillText('NG', 'scratch', 'classification', nameOf)).toEqual({ text: '긁힘' })
  })

  it('다중 분류여도 레코드에 클래스가 없으면(옛 레코드) OK/NG 로 돌아간다', () => {
    expect(verdictPillText('OK', undefined, 'classification', nameOf)).toEqual({ text: 'OK' })
  })
})
