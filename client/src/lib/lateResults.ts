import type { InspectResult } from './types'

/**
 * 큐에 쌓였다가 늦게 판정된 결과 가운데 운영자가 확인해야 할 것 — NG(오투입 확인이면 불일치). 늦은 결과는 현재
 * 판정 · 차단 게이트를 덮지 않으므로(지난 부품이다), 대신 확인할 때까지 남는 알림으로 따로 모은다.
 */
export function addLateAlert(alerts: InspectResult[], result: InspectResult): InspectResult[] {
  if (result.verdict !== 'NG') return alerts
  if (alerts.some((a) => a.image_id === result.image_id)) return alerts
  return [...alerts, result]
}
