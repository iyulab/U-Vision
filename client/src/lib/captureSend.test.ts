import { describe, expect, it, vi } from 'vitest'

import type { QueuedCapture } from './captureQueue'
import { sendCapture } from './captureSend'

const api = vi.hoisted(() => ({
  inspectImage: vi.fn(async () => ({ verdict: 'OK', findings: '', confidence: 1, timestamp: 't', image_id: 'img_1' })),
  inspectImageV2: vi.fn(async () => ({ decision_id: 'img_2', scenario_id: 's', timestamp: 't', class: 'MATCH' })),
}))
vi.mock('./api', () => api)

const item = (extra: Partial<QueuedCapture> = {}): QueuedCapture => ({
  id: 'cap-1',
  bytes: new Uint8Array([1]).buffer,
  type: 'image/jpeg',
  scenarioId: 's',
  deviceId: 'd',
  deviceLabel: 'l',
  capturedAt: '2026-10-22T01:00:00.000Z',
  ...extra,
})

describe('sendCapture', () => {
  it('큐 항목 id 를 멱등 키로 넘긴다 — 검사는 v1', async () => {
    await sendCapture(item())
    expect(api.inspectImage).toHaveBeenCalledWith(expect.any(Blob), 's', 'd', 'l', '2026-10-22T01:00:00.000Z', 'cap-1')
  })

  it('다중 분류는 v1 이 아니라 v2 로 보내고, 결과를 캡처 때 선언한 클래스로 푼다', async () => {
    api.inspectImage.mockClear()
    api.inspectImageV2.mockClear()
    api.inspectImageV2.mockResolvedValueOnce({ decision_id: 'img_3', scenario_id: 's', timestamp: 't', class: 'reject' })
    const result = await sendCapture(item({ classification: { classes: [{ id: 'A100' }, { id: 'reject', label: '불량', must_catch: true }] } }))
    expect(api.inspectImage).not.toHaveBeenCalled()
    expect(api.inspectImageV2).toHaveBeenCalledWith(expect.any(Blob), 's', 'd', 'l', {}, '2026-10-22T01:00:00.000Z', 'cap-1')
    expect(result).toMatchObject({ verdict: 'NG', class_name: '불량', image_id: 'img_3' })
  })

  it('오투입 확인은 v2 로, 같은 키로', async () => {
    await sendCapture(item({ expectedPart: 'P-100', observedCode: 'P-100' }))
    expect(api.inspectImageV2).toHaveBeenCalledWith(
      expect.any(Blob), 's', 'd', 'l', { expectedPart: 'P-100', observedCode: 'P-100' }, '2026-10-22T01:00:00.000Z', 'cap-1')
  })
})
