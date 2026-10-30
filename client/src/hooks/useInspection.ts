import { useCallback, useEffect, useEffectEvent, useRef, useState, type RefObject } from 'react'

import { readCode } from '../lib/barcode'
import { captureFrame } from '../lib/capture'
import type { QueuedCapture } from '../lib/captureQueue'
import { getCaptureSender, sendCapture } from '../lib/captureSend'
import { type InspectionPhase } from '../lib/capturePolicy'
import { getDeviceId, getDeviceLabel } from '../lib/deviceIdentity'
import { classifyTriggerError } from '../lib/inspectionError'
import { addLateAlert } from '../lib/lateResults'
import type { SendOutcome } from '../lib/queueSender'
import type { Roi } from '../lib/roi'
import type { InspectResult, MlResult } from '../lib/types'

export type { InspectionPhase } from '../lib/capturePolicy'

export interface InspectionState {
  phase: InspectionPhase
  latest: InspectResult | null
  history: InspectResult[]
  error: string | null
  /** 판정 불가(fail-closed) — VLM 검출원 사용 불가(③.5 E2). 정상 시 null. */
  unavailable: { reason: string; mlHint?: MlResult } | null
  /** 기기 큐에 남아 서버를 기다리는 캡처 수. */
  pending: number
  /** 기기가 큐 저장소를 지켜 주는가 — false 면 대기 중인 캡처가 브라우저 정리로 사라질 수 있다. 모름은 null. */
  storageDurable: boolean | null
  /** 큐에서 늦게 보낸 캡처가 서버에서 거부됐을 때의 알림(현재 캡처의 오류와 따로 둔다). */
  queueNotice: string | null
  /** 늦게 판정된 NG — 운영자가 확인할 때까지 남는다(현재 판정 · 차단 게이트와 따로). */
  lateAlerts: InspectResult[]
  /** 늦은 NG 알림을 확인했다. */
  dismissLateAlerts: () => void
  /** 캡처→업로드→판정 1회 실행. 진행 중이면 무시(중복 트리거 방지). */
  trigger: () => void
  /** 차단 게이트(co-primary 불일치) 해제 — 작업자 명시 확인. blocked → done. */
  acknowledge: () => void
}

const HISTORY_LIMIT = 20
/** 큐가 비어 있지 않을 때 다시 보내 보는 간격 — 포그라운드 재시도(iOS 는 백그라운드 동기화가 없다). */
const RETRY_INTERVAL_MS = 15_000

/**
 * 검사 1회의 수명주기 상태머신.
 *
 * 흐림 거부: 캡처 선명도가 minSharpness 미만이면 업로드 없이 거부(VLM 호출 전 차단).
 * 오투입 확인(`expectedPart` 가 문자열): 기대 품번이 비면 업로드 없이 거부하고, 있으면 캡처에서 코드를 읽어
 * 결과 v2 경로로 판정한다. 검사 시나리오는 `expectedPart` 를 null 로 둔다.
 *
 * 오프라인 큐: 캡처는 먼저 기기 큐(IndexedDB)에 적고 곧바로 보낸다. 서버에 닿지 못하면 `queued` 로 두고 다음
 * 캡처를 받는다 — 연결이 돌아오거나(online) 주기 재시도 때 먼저 찍은 것부터 보내고, 그 결과는 `late` 로
 * 이력에만 들어간다(지금 화면의 판정 · 차단 게이트를 덮지 않는다). 서버가 답한 실패(검증 거부 · 판정 불가)는
 * 다시 보내지 않는다 — fail-closed 는 «나중에 다시»가 아니다.
 */
export function useInspection(
  videoRef: RefObject<HTMLVideoElement | null>,
  scenarioId: string,
  roi: Roi,
  minSharpness: number,
  expectedPart: string | null = null,
): InspectionState {
  const [phase, setPhase] = useState<InspectionPhase>('idle')
  const [latest, setLatest] = useState<InspectResult | null>(null)
  const [history, setHistory] = useState<InspectResult[]>([])
  const [error, setError] = useState<string | null>(null)
  const [unavailable, setUnavailable] = useState<{ reason: string; mlHint?: MlResult } | null>(null)
  const [pending, setPending] = useState(0)
  const [storageDurable, setStorageDurable] = useState<boolean | null>(null)
  const [queueNotice, setQueueNotice] = useState<string | null>(null)
  const [lateAlerts, setLateAlerts] = useState<InspectResult[]>([])
  const inFlight = useRef(false)
  /** 지금 화면이 기다리는 캡처 — 큐 비우기가 먼저 보내도 이 캡처면 화면 판정으로 낸다. */
  const currentId = useRef<string | null>(null)
  /** 재시도 타이머가 지금 대기 건수를 읽는다(타이머를 다시 걸지 않고). */
  const hasPending = useEffectEvent(() => pending > 0)

  const refreshPending = useCallback(async () => {
    const sender = await getCaptureSender()
    setPending(sender ? await sender.count() : 0)
  }, [])

  const showResult = useCallback((result: InspectResult) => {
    setLatest(result)
    setHistory((h) => [result, ...h].slice(0, HISTORY_LIMIT))
    // co-primary 불일치 = 차단 게이트: 명시 확인 전까지 다음 캡처 차단(blocked).
    setPhase(result.posture === 'review_block' ? 'blocked' : 'done')
  }, [])

  const showFailure = useCallback((e: unknown) => {
    const c = classifyTriggerError(e)
    setUnavailable(c.unavailable)
    setError(c.error)
    setPhase(c.phase)
  }, [])

  const onDrained = useCallback(
    (o: SendOutcome<InspectResult>) => {
      if (o.kind === 'sent' && o.item.id === currentId.current) {
        currentId.current = null
        showResult(o.result)
      } else if (o.kind === 'failed' && o.item.id === currentId.current) {
        currentId.current = null
        showFailure(o.error)
      } else if (o.kind === 'sent') {
        const late = { ...o.result, late: true, captured_at: o.item.capturedAt }
        setHistory((h) => [late, ...h].slice(0, HISTORY_LIMIT))
        setLateAlerts((a) => addLateAlert(a, late))
      } else if (o.kind === 'failed') {
        setQueueNotice(`대기했던 캡처(${new Date(o.item.capturedAt).toLocaleTimeString('ko-KR')}) 판정 실패 — ${classifyTriggerError(o.error).error ?? '판정 불가'}`)
      }
    },
    [showResult, showFailure],
  )

  // 쌓인 캡처 보내기 — 처음 · 다시 연결됐을 때 · 큐가 남아 있는 동안 주기적으로.
  useEffect(() => {
    let cancelled = false
    const drain = async () => {
      const sender = await getCaptureSender()
      if (!sender || cancelled) return
      setStorageDurable(sender.durable)
      await sender.drain(onDrained)
      if (cancelled) return
      const left = await sender.count()
      setPending(left)
      // 다 보냈으면 «오프라인 — 저장됨» 상태를 내린다(다음 캡처를 기다리는 상태로).
      if (left === 0) setPhase((p) => (p === 'queued' ? 'idle' : p))
    }
    void drain()
    window.addEventListener('online', drain)
    const timer = window.setInterval(() => {
      if (hasPending()) void drain()
    }, RETRY_INTERVAL_MS)
    return () => {
      cancelled = true
      window.removeEventListener('online', drain)
      window.clearInterval(timer)
    }
  }, [onDrained])

  const trigger = useCallback(() => {
    if (inFlight.current) return
    const video = videoRef.current
    if (!video) return
    inFlight.current = true

    void (async () => {
      setError(null)
      setUnavailable(null)
      try {
        setPhase('capturing')
        const { blob, sharpness } = await captureFrame(video, roi)
        // 흐림 거부: VLM 호출 전 차단(토큰 절약). minSharpness<=0 이면 비활성.
        if (minSharpness > 0 && sharpness < minSharpness) {
          setError(`흐림 — 재시도 (선명도 ${Math.round(sharpness)} < ${minSharpness})`)
          setPhase('rejected')
          return
        }
        const item: QueuedCapture = {
          id: crypto.randomUUID(),
          bytes: await blob.arrayBuffer(),
          type: blob.type || 'image/jpeg',
          scenarioId,
          deviceId: getDeviceId(),
          deviceLabel: getDeviceLabel(),
          capturedAt: new Date().toISOString(),
        }
        if (expectedPart !== null) {
          const expected = expectedPart.trim()
          if (!expected) {
            setError('기대 품번을 입력하세요')
            setPhase('rejected')
            return
          }
          item.expectedPart = expected
          item.observedCode = await readCode(blob)
        }
        setPhase('uploading')

        const sender = await getCaptureSender()
        const queued = sender ? await sender.add(item).then(() => true, () => false) : false
        if (!sender || !queued) {
          // 큐를 쓸 수 없는 환경 — 바로 보낸다(실패는 그대로 보인다).
          showResult(await sendCapture(item))
          return
        }
        currentId.current = item.id
        const outcome = await sender.sendOne(item.id)
        if (outcome.kind === 'sent') {
          currentId.current = null
          showResult(outcome.result)
        } else if (outcome.kind === 'failed') {
          currentId.current = null
          showFailure(outcome.error)
        } else if (outcome.kind === 'offline' || outcome.kind === 'retry') {
          // 화면은 다음 캡처로 넘어간다 — 이 캡처의 판정은 나중에 이력으로만(차단 게이트를 걸지 않는다).
          // retry(서버가 저장 못 함)도 같다: 큐에 남아 다시 보낸다.
          currentId.current = null
          setPhase('queued')
        }
        // 'gone' — 큐 비우기가 먼저 보냈고 onDrained 가 이미 화면에 냈다.
        await refreshPending()
      } catch (e) {
        showFailure(e)
      } finally {
        inFlight.current = false
      }
    })()
  }, [videoRef, scenarioId, roi, minSharpness, expectedPart, showResult, showFailure, refreshPending])

  const acknowledge = useCallback(() => {
    setPhase((p) => (p === 'blocked' ? 'done' : p))
  }, [])

  const dismissLateAlerts = useCallback(() => setLateAlerts([]), [])

  return {
    phase,
    latest,
    history,
    error,
    unavailable,
    pending,
    storageDurable,
    queueNotice,
    lateAlerts,
    dismissLateAlerts,
    trigger,
    acknowledge,
  }
}
