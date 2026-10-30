import 'fake-indexeddb/auto'
import { describe, expect, it, vi } from 'vitest'

import { ApiError, DetectionUnavailableError, NetworkError } from './api'
import { openCaptureQueue, type QueuedCapture } from './captureQueue'
import { createQueueSender, type SendOutcome } from './queueSender'

let dbSeq = 0
const freshQueue = () => openCaptureQueue(`test-queue-${++dbSeq}`)

const item = (id: string, capturedAt: string): QueuedCapture => ({
  id,
  bytes: new Uint8Array([1, 2, 3]).buffer,
  type: 'image/jpeg',
  scenarioId: 's',
  deviceId: 'd',
  deviceLabel: 'l',
  capturedAt,
})

describe('captureQueue', () => {
  it('넣은 항목을 캡처 시각 순으로 돌려주고 지운다', async () => {
    const q = await freshQueue()
    await q.add(item('b', '2026-10-22T01:00:01.000Z'))
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    expect(await q.ids()).toEqual(['a', 'b'])
    expect(await q.count()).toBe(2)
    expect(new Uint8Array((await q.get('a'))!.bytes)).toEqual(new Uint8Array([1, 2, 3]))
    await q.remove('a')
    expect(await q.ids()).toEqual(['b'])
  })
})

describe('queueSender', () => {
  it('보내면 큐에서 빠진다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    const sender = createQueueSender(q, async () => 'ok')
    expect(await sender.sendOne('a')).toMatchObject({ kind: 'sent', result: 'ok' })
    expect(await q.count()).toBe(0)
  })

  it('서버에 닿지 못하면 남는다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    const sender = createQueueSender(q, async () => {
      throw new NetworkError(new TypeError('Failed to fetch'))
    })
    expect(await sender.sendOne('a')).toEqual({ kind: 'offline' })
    expect(await q.count()).toBe(1)
  })

  it('서버가 답한 실패(판정 불가 · 검증 거부)는 다시 보내지 않는다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    const sender = createQueueSender(q, async () => {
      throw new ApiError(400, 'bad')
    })
    expect(await sender.sendOne('a')).toMatchObject({ kind: 'failed' })
    expect(await q.count()).toBe(0)
  })

  it.each([500, 502, 503, 504])('서버가 %i 로 아무것도 결정하지 못하면 큐에 남아 다시 보낸다', async (status) => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    const sender = createQueueSender(q, async () => {
      throw new ApiError(status, '저장 실패')
    })
    expect(await sender.sendOne('a')).toMatchObject({ kind: 'retry' })
    expect(await q.count()).toBe(1)
  })

  it.each([400, 404, 413, 415])('서버의 거부(%i)는 다시 보내지 않는다', async (status) => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    const sender = createQueueSender(q, async () => {
      throw new ApiError(status, 'rejected')
    })
    expect(await sender.sendOne('a')).toMatchObject({ kind: 'failed' })
    expect(await q.count()).toBe(0)
  })

  it('판정 불가(fail-closed)는 서버의 결정이라 다시 보내지 않는다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    const sender = createQueueSender(q, async () => {
      throw new DetectionUnavailableError('vlm_down')
    })
    expect(await sender.sendOne('a')).toMatchObject({ kind: 'failed' })
    expect(await q.count()).toBe(0)
  })

  it('저장 실패로 남은 항목이 줄을 막지 않고, 서버가 회복하면 다음 drain 에서 나간다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    await q.add(item('b', '2026-10-22T01:00:01.000Z'))
    let storageOk = false
    const sender = createQueueSender(q, async (it) => {
      if (it.id === 'a' && !storageOk) throw new ApiError(500, '검사 결과 저장 실패')
      return it.id
    })
    const first: SendOutcome<string>[] = []
    await sender.drain((o) => first.push(o))
    expect(first.map((o) => o.kind)).toEqual(['retry', 'sent'])
    expect(await q.ids()).toEqual(['a'])

    storageOk = true
    const second: SendOutcome<string>[] = []
    await sender.drain((o) => second.push(o))
    expect(second.map((o) => o.kind)).toEqual(['sent'])
    expect(await q.count()).toBe(0)
  })

  it('drain 은 먼저 찍은 것부터 보내고, 다시 끊기면 멈춘다', async () => {
    const q = await freshQueue()
    await q.add(item('c', '2026-10-22T01:00:02.000Z'))
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    await q.add(item('b', '2026-10-22T01:00:01.000Z'))
    const sent: string[] = []
    const sender = createQueueSender(q, async (it) => {
      if (it.id === 'c') throw new NetworkError(null)
      sent.push(it.id)
      return it.id
    })
    const outcomes: SendOutcome<string>[] = []
    await sender.drain((o) => outcomes.push(o))
    expect(sent).toEqual(['a', 'b'])
    expect(outcomes.map((o) => o.kind)).toEqual(['sent', 'sent'])
    expect(await q.ids()).toEqual(['c'])
  })

  it('같은 항목을 동시에 두 번 보내지 않는다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    const send = vi.fn(async () => 'ok')
    const sender = createQueueSender(q, send)
    const [x, y] = await Promise.all([sender.sendOne('a'), sender.sendOne('a')])
    expect(send).toHaveBeenCalledTimes(1)
    expect([x.kind, y.kind].sort()).toEqual(['gone', 'sent'])
  })
})
