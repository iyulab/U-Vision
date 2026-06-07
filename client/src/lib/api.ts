import type { InspectResult } from './types'

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? ''

/** 캡처 이미지를 서버로 보내 판정 결과를 받는다. */
export async function inspectImage(image: Blob, scenarioId: string): Promise<InspectResult> {
  const form = new FormData()
  form.append('image', image, 'capture.jpg')
  form.append('scenario_id', scenarioId)

  const res = await fetch(`${API_BASE}/api/inspect`, { method: 'POST', body: form })
  if (!res.ok) {
    const detail = await res.text().catch(() => '')
    throw new Error(`판정 요청 실패 (${res.status}) ${detail}`.trim())
  }
  return (await res.json()) as InspectResult
}
