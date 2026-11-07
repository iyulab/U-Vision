import 'fake-indexeddb/auto'
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ApiError, DetectionUnavailableError, NetworkError } from '../lib/api'
import { openCaptureQueue, type QueuedCapture } from '../lib/captureQueue'
import type { CaptureSender } from '../lib/captureSend'
import { createQueueSender } from '../lib/queueSender'
import type { InspectResult } from '../lib/types'
import { useInspection } from './useInspection'

// 훅의 상태 전이만 본다 — 카메라 캡처 · 코드 읽기 · 서버 호출은 바꿔 끼우고, 큐(IndexedDB)와 보내기 통로는 진짜를 쓴다.
const h = vi.hoisted(() => ({
  sharpness: 100,
  durable: true as boolean | null,
  send: vi.fn<(item: QueuedCapture) => Promise<InspectResult>>(),
  sender: null as Promise<CaptureSender | null> | null,
}))

vi.mock('../lib/capture', () => ({
  captureFrame: async () => ({ blob: new Blob([new Uint8Array([1, 2, 3])], { type: 'image/jpeg' }), sharpness: h.sharpness }),
}))
vi.mock('../lib/barcode', () => ({ readCode: async () => 'P-100' }))
vi.mock('../lib/captureSend', () => ({
  sendCapture: (item: QueuedCapture) => h.send(item),
  getCaptureSender: () => h.sender,
}))

let dbSeq = 0
async function useRealQueue() {
  const queue = await openCaptureQueue(`hook-queue-${++dbSeq}`)
  const sender: CaptureSender = { ...createQueueSender(queue, (item) => h.send(item)), count: queue.count, add: queue.add, durable: h.durable }
  h.sender = Promise.resolve(sender)
  return queue
}

const result = (verdict: 'OK' | 'NG', extra: Partial<InspectResult> = {}): InspectResult => ({
  verdict,
  findings: '',
  confidence: 0.9,
  timestamp: '2026-10-22T01:00:00Z',
  image_id: `img-${Math.random()}`,
  ...extra,
})

const video = { current: {} as HTMLVideoElement }
const roi = { x: 0, y: 0, width: 0, height: 0 }

function render(minSharpness = 0, expectedPart: string | null = null) {
  return renderHook(() => useInspection(video, 's', roi, minSharpness, expectedPart))
}

/** 마운트 때의 첫 큐 비우기가 끝나기를 기다린다 — 끝나기 전에 트리거하면 그 비우기가 방금 캡처를 먼저 보낸다. */
async function mounted(minSharpness = 0, expectedPart: string | null = null) {
  const hook = render(minSharpness, expectedPart)
  await act(async () => {
    await h.sender
    await new Promise((r) => setTimeout(r, 0))
  })
  return hook
}

// vitest 전역(globals)을 켜지 않아 RTL 의 자동 정리가 걸리지 않는다 — 앞 테스트의 훅이 남아 'online' 을 함께 받지 않게.
afterEach(cleanup)

beforeEach(() => {
  h.sharpness = 100
  h.durable = true
  h.send.mockReset()
  h.sender = null
})

describe('useInspection', () => {
  it('보낸 캡처의 판정을 화면에 내고 큐를 비운다', async () => {
    const queue = await useRealQueue()
    h.send.mockResolvedValue(result('OK'))
    const { result: hook } = await mounted()

    act(() => hook.current.trigger())

    await waitFor(() => expect(hook.current.phase).toBe('done'))
    expect(hook.current.latest?.verdict).toBe('OK')
    expect(hook.current.history).toHaveLength(1)
    expect(hook.current.pending).toBe(0)
    expect(await queue.count()).toBe(0)
  })

  it('고른 사진 파일도 같은 경로(큐 → 서버)로 판정하고, 흐림 거부는 걸지 않는다', async () => {
    const queue = await useRealQueue()
    h.sharpness = 1 // 카메라 프레임이었다면 거부되었을 선명도 — 파일에는 적용하지 않는다
    h.send.mockResolvedValue(result('NG'))
    const { result: hook } = await mounted(50)
    const file = new File([new Uint8Array([9, 8, 7])], 'part.png', { type: 'image/png' })

    act(() => hook.current.uploadFile(file))

    await waitFor(() => expect(hook.current.phase).toBe('done'))
    expect(hook.current.latest?.verdict).toBe('NG')
    expect(h.send).toHaveBeenCalledTimes(1)
    const sent = h.send.mock.calls[0][0]
    expect(sent.type).toBe('image/png')
    expect(sent.scenarioId).toBe('s')
    expect(new Uint8Array(sent.bytes)).toEqual(new Uint8Array([9, 8, 7]))
    expect(await queue.count()).toBe(0)
  })

  it('JPEG · PNG 가 아닌 파일은 보내지 않고 이유를 말한다', async () => {
    await useRealQueue()
    const { result: hook } = await mounted()

    act(() => hook.current.uploadFile(new File(['x'], 'notes.txt', { type: 'text/plain' })))

    expect(hook.current.phase).toBe('rejected')
    expect(hook.current.error).toContain('JPEG 또는 PNG')
    expect(h.send).not.toHaveBeenCalled()
  })

  it('서버에 닿지 못할 때 고른 파일도 기기 큐에 남았다가 돌아오면 보낸다 — 중복 없이', async () => {
    const queue = await useRealQueue()
    h.send.mockRejectedValueOnce(new NetworkError('offline')).mockResolvedValue(result('OK'))
    const { result: hook } = await mounted()

    act(() => hook.current.uploadFile(new File([new Uint8Array([1])], 'a.jpg', { type: 'image/jpeg' })))
    await waitFor(() => expect(hook.current.phase).toBe('queued'))
    expect(await queue.count()).toBe(1)

    await act(async () => {
      window.dispatchEvent(new Event('online'))
      await new Promise((r) => setTimeout(r, 20))
    })
    await waitFor(() => expect(hook.current.pending).toBe(0))
    expect(h.send).toHaveBeenCalledTimes(2) // 한 번 실패 + 한 번 성공 — 같은 항목을 둘로 보내지 않는다
    expect(hook.current.history).toHaveLength(1)
  })

  it('차단 게이트 판정은 확인 전까지 blocked 로 남는다', async () => {
    await useRealQueue()
    h.send.mockResolvedValue(result('NG', { posture: 'review_block' }))
    const { result: hook } = await mounted()

    act(() => hook.current.trigger())
    await waitFor(() => expect(hook.current.phase).toBe('blocked'))

    act(() => hook.current.acknowledge())
    expect(hook.current.phase).toBe('done')
  })

  it('흐린 캡처는 보내지 않고 거부한다', async () => {
    await useRealQueue()
    h.sharpness = 10
    const { result: hook } = await mounted(50)

    act(() => hook.current.trigger())

    await waitFor(() => expect(hook.current.phase).toBe('rejected'))
    expect(hook.current.error).toContain('흐림')
    expect(h.send).not.toHaveBeenCalled()
  })

  it('오투입 확인에서 기대 품번이 비면 보내지 않는다', async () => {
    await useRealQueue()
    const { result: hook } = await mounted(0, '  ')

    act(() => hook.current.trigger())

    await waitFor(() => expect(hook.current.phase).toBe('rejected'))
    expect(hook.current.error).toBe('기대 품번을 입력하세요')
    expect(h.send).not.toHaveBeenCalled()
  })

  it('다중 분류는 선언한 클래스를 큐 항목에 실어 보낸다 — 오투입 확인의 품번은 싣지 않는다', async () => {
    await useRealQueue()
    h.send.mockResolvedValue(result('OK', { class_name: '품번 A' }))
    const classes = [{ id: 'A100', label: '품번 A' }, { id: 'reject', must_catch: true }]
    const { result: hook } = renderHook(() => useInspection(video, 's', roi, 0, null, classes))
    await act(async () => {
      await h.sender
      await new Promise((r) => setTimeout(r, 0))
    })

    act(() => hook.current.trigger())

    await waitFor(() => expect(hook.current.phase).toBe('done'))
    const sent = h.send.mock.calls[0][0]
    expect(sent.classification).toEqual({ classes })
    expect(sent.expectedPart).toBeUndefined()
    expect(hook.current.latest?.class_name).toBe('품번 A')
  })

  it('오투입 확인은 기대 품번과 읽은 코드를 함께 보낸다', async () => {
    await useRealQueue()
    h.send.mockResolvedValue(result('OK'))
    const { result: hook } = await mounted(0, ' P-100 ')

    act(() => hook.current.trigger())

    await waitFor(() => expect(hook.current.phase).toBe('done'))
    expect(h.send.mock.calls[0][0]).toMatchObject({ expectedPart: 'P-100', observedCode: 'P-100' })
  })

  it('판정 불가는 unavailable 로 두고 다시 보내지 않는다', async () => {
    const queue = await useRealQueue()
    h.send.mockRejectedValue(new DetectionUnavailableError('vlm down'))
    const { result: hook } = await mounted()

    act(() => hook.current.trigger())

    await waitFor(() => expect(hook.current.phase).toBe('unavailable'))
    expect(hook.current.unavailable?.reason).toBe('vlm down')
    expect(await queue.count()).toBe(0)
  })

  it('서버에 닿지 못하면 queued 로 두고, 연결이 돌아와 다 보내면 idle 로 내린다', async () => {
    const queue = await useRealQueue()
    h.send.mockRejectedValue(new NetworkError(new TypeError('fetch failed')))
    const { result: hook } = await mounted()

    act(() => hook.current.trigger())
    await waitFor(() => expect(hook.current.phase).toBe('queued'))
    await waitFor(() => expect(hook.current.pending).toBe(1))
    expect(await queue.count()).toBe(1)

    const late = result('NG')
    h.send.mockResolvedValue(late)
    await act(async () => {
      window.dispatchEvent(new Event('online'))
    })

    await waitFor(() => expect(hook.current.pending).toBe(0))
    expect(hook.current.phase).toBe('idle')
    // 늦은 판정은 이력과 알림에만 — 현재 판정(latest)을 덮지 않는다.
    expect(hook.current.latest).toBeNull()
    expect(hook.current.history[0]).toMatchObject({ image_id: late.image_id, late: true })
    expect(hook.current.history[0].captured_at).toBeTruthy()
    expect(hook.current.lateAlerts.map((a) => a.image_id)).toEqual([late.image_id])

    act(() => hook.current.dismissLateAlerts())
    expect(hook.current.lateAlerts).toEqual([])
  })

  it('서버가 저장에 실패(500)해도 캡처를 버리지 않고 queued 로 두었다가 회복되면 보낸다', async () => {
    const queue = await useRealQueue()
    h.send.mockRejectedValue(new ApiError(500, '판정 요청 실패 (500) 검사 결과 저장 실패'))
    const { result: hook } = await mounted()

    act(() => hook.current.trigger())
    await waitFor(() => expect(hook.current.phase).toBe('queued'))
    expect(hook.current.error).toBeNull()
    expect(await queue.count()).toBe(1)

    const late = result('OK')
    h.send.mockResolvedValue(late)
    await act(async () => {
      window.dispatchEvent(new Event('online'))
    })

    await waitFor(() => expect(hook.current.pending).toBe(0))
    expect(await queue.count()).toBe(0)
    expect(hook.current.history[0]).toMatchObject({ image_id: late.image_id, late: true })
  })

  it('서버가 계속 받아들이지 않는 캡처는 알리고, 풀리면 그 알림을 걷는다', async () => {
    const queue = await useRealQueue()
    h.send.mockRejectedValue(new ApiError(500, '판정 요청 실패 (500) 검사 결과 저장 실패'))
    const { result: hook } = await mounted()

    act(() => hook.current.trigger())
    await waitFor(() => expect(hook.current.phase).toBe('queued'))
    // 연결 신호(online)는 기다리지 않고 다시 보내므로 실패가 쌓여 «막힌 항목» 이 된다.
    for (let i = 0; i < 5; i++)
      await act(async () => {
        window.dispatchEvent(new Event('online'))
      })
    await waitFor(() => expect(hook.current.queueNotice).toContain('계속 받지 못하고'))
    expect(await queue.count()).toBe(1)

    h.send.mockResolvedValue(result('OK'))
    await act(async () => {
      window.dispatchEvent(new Event('online'))
    })

    await waitFor(() => expect(hook.current.pending).toBe(0))
    expect(hook.current.queueNotice).toBeNull()
  })

  it('늦게 보낸 캡처를 서버가 거부하면 알림만 남기고 큐에서 뺀다', async () => {
    const queue = await useRealQueue()
    h.send.mockRejectedValue(new NetworkError(new TypeError('fetch failed')))
    const { result: hook } = await mounted()

    act(() => hook.current.trigger())
    await waitFor(() => expect(hook.current.pending).toBe(1))

    h.send.mockRejectedValue(new Error('잘못된 요청'))
    await act(async () => {
      window.dispatchEvent(new Event('online'))
    })

    await waitFor(() => expect(hook.current.queueNotice).toContain('잘못된 요청'))
    expect(hook.current.pending).toBe(0)
    expect(hook.current.phase).toBe('idle')
    expect(await queue.count()).toBe(0)
  })

  it('기기가 저장을 지켜 주지 않으면 그 사실을 알린다', async () => {
    h.durable = false
    await useRealQueue()
    const { result: hook } = await mounted()
    expect(hook.current.storageDurable).toBe(false)
  })

  it('큐를 쓸 수 없으면 바로 보낸다', async () => {
    h.sender = Promise.resolve(null)
    h.send.mockResolvedValue(result('OK'))
    const { result: hook } = await mounted()

    act(() => hook.current.trigger())

    await waitFor(() => expect(hook.current.phase).toBe('done'))
    expect(h.send).toHaveBeenCalledTimes(1)
  })
})
