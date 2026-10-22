import { inspectImage, inspectImageV2 } from './api'
import { openCaptureQueue, type QueuedCapture } from './captureQueue'
import { createQueueSender } from './queueSender'
import type { InspectResult } from './types'
import { toInspectResult } from './verification'

/** 큐 항목 하나를 서버로 보낸다 — 오투입 확인(기대 품번 있음)은 결과 v2, 그 밖은 v1. */
export async function sendCapture(item: QueuedCapture): Promise<InspectResult> {
  const blob = new Blob([item.bytes], { type: item.type })
  if (item.expectedPart !== undefined)
    return toInspectResult(
      await inspectImageV2(
        blob,
        item.scenarioId,
        item.deviceId,
        item.deviceLabel,
        { expectedPart: item.expectedPart, observedCode: item.observedCode },
        item.capturedAt,
      ),
    )
  return inspectImage(blob, item.scenarioId, item.deviceId, item.deviceLabel, item.capturedAt)
}

export type CaptureSender = ReturnType<typeof createQueueSender<InspectResult>> & {
  count: () => Promise<number>
  add: (item: QueuedCapture) => Promise<void>
}

let sender: Promise<CaptureSender | null> | undefined

/**
 * 앱 전체에 하나뿐인 보내기 통로(같은 항목이 두 통로로 나가지 않게). IndexedDB 를 열 수 없으면 null —
 * 호출부는 큐 없이 바로 보낸다(오늘의 동작).
 */
export function getCaptureSender(): Promise<CaptureSender | null> {
  sender ??= openCaptureQueue()
    .then((queue) => {
      // Safari 등은 오래 안 쓴 사이트 저장소를 비운다 — 지속 저장을 한 번 요청한다(거절돼도 큐는 동작).
      void navigator.storage?.persist?.().catch(() => undefined)
      return { ...createQueueSender(queue, sendCapture), count: queue.count, add: queue.add }
    })
    .catch(() => null)
  return sender
}
