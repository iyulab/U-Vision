import 'fake-indexeddb/auto'
import { describe, expect, it, vi } from 'vitest'

import { ApiError, DetectionUnavailableError, NetworkError } from './api'
import { openCaptureQueue, type QueuedCapture } from './captureQueue'
import { createQueueSender, isStuckNotice, retryDelayMs, type SendOutcome, STUCK_AFTER_FAILURES, stuckNotice } from './queueSender'

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
    let clock = 0
    const sender = createQueueSender(
      q,
      async (it) => {
        if (it.id === 'a' && !storageOk) throw new ApiError(500, '검사 결과 저장 실패')
        return it.id
      },
      () => clock,
    )
    const first: SendOutcome<string>[] = []
    await sender.drain((o) => first.push(o))
    expect(first.map((o) => o.kind)).toEqual(['retry', 'sent'])
    expect(await q.ids()).toEqual(['a'])

    storageOk = true
    clock += 15_000 // 다음 주기 재시도 — 첫 실패 뒤 기다리는 시간이 지났다
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

describe('queueSender 재시도 간격', () => {
  it('실패할수록 두 배로 늘고 10분에서 멈춘다', () => {
    expect([1, 2, 3, 4].map(retryDelayMs)).toEqual([15_000, 30_000, 60_000, 120_000])
    expect(retryDelayMs(7)).toBe(600_000)
    expect(retryDelayMs(50)).toBe(600_000)
    expect(retryDelayMs(0)).toBe(15_000)
  })

  it('받아들여지지 않은 항목은 다음 시도 시각 전의 주기 재시도에서 건너뛰고, force 는 기다리지 않는다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    let clock = 1_000_000
    const send = vi.fn(async () => {
      throw new ApiError(500, '저장 실패')
    })
    const sender = createQueueSender(q, send, () => clock)
    const seen: string[] = []
    const onOutcome = (o: SendOutcome<unknown>) => seen.push(o.kind)

    await sender.drain(onOutcome) // 첫 시도 — 실패, 다음 시도는 15초 뒤
    await sender.drain(onOutcome) // 아직 이르다 — 건너뜀
    expect(send).toHaveBeenCalledTimes(1)

    clock += 15_000
    await sender.drain(onOutcome) // 때가 됐다 — 두 번째 실패, 다음은 30초 뒤
    expect(send).toHaveBeenCalledTimes(2)
    clock += 29_000
    await sender.drain(onOutcome)
    expect(send).toHaveBeenCalledTimes(2)

    await sender.drain(onOutcome, { force: true }) // 연결이 돌아왔다는 신호는 기다리지 않는다
    expect(send).toHaveBeenCalledTimes(3)
    expect(seen).toEqual(['retry', 'retry', 'retry'])
    expect(await q.count()).toBe(1)
  })

  it('연결이 끊긴 실패(NetworkError)는 실패 횟수에 들지 않는다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    const send = vi.fn(async () => {
      throw new NetworkError(new TypeError('fetch failed'))
    })
    const sender = createQueueSender(q, send, () => 1)

    await sender.drain(() => undefined)
    await sender.drain(() => undefined)

    expect(send).toHaveBeenCalledTimes(2) // 연결이 안 되는 동안은 기다리지 않고 매 drain 이 다시 시도한다
    expect(sender.stuckCount()).toBe(0)
  })

  it('연달아 받아들여지지 않은 항목은 막힌 항목으로 세고, 보내지면 걷힌다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    let ok = false
    const sender = createQueueSender(q, async () => {
      if (!ok) throw new ApiError(500, '저장 실패')
      return 'sent'
    }, () => 0)

    for (let i = 0; i < STUCK_AFTER_FAILURES - 1; i++) await sender.sendOne('a')
    expect(sender.stuckCount()).toBe(0)
    await sender.sendOne('a')
    expect(sender.stuckCount()).toBe(1)

    ok = true
    await sender.sendOne('a')
    expect(sender.stuckCount()).toBe(0)
    expect(await q.count()).toBe(0)
  })

  it('서버가 거부(400)한 항목은 큐에서 빠지면서 실패 기록도 지운다', async () => {
    const q = await freshQueue()
    await q.add(item('a', '2026-10-22T01:00:00.000Z'))
    let status = 500
    const sender = createQueueSender(q, async () => {
      throw new ApiError(status, 'x')
    }, () => 0)

    for (let i = 0; i < STUCK_AFTER_FAILURES; i++) await sender.sendOne('a')
    expect(sender.stuckCount()).toBe(1)
    status = 400
    await sender.sendOne('a')

    expect(await q.count()).toBe(0)
    expect(sender.stuckCount()).toBe(0)
  })

  it('알림 문구는 막힌 항목이 있을 때만 있고, 그 문구만 알아본다', () => {
    expect(stuckNotice(0)).toBeNull()
    const text = stuckNotice(2)
    expect(text).toContain('2건')
    expect(isStuckNotice(text)).toBe(true)
    expect(isStuckNotice('대기했던 캡처(09:00) 판정 실패 — 잘못된 요청')).toBe(false)
    expect(isStuckNotice(null)).toBe(false)
  })
})

