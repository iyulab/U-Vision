/** 서버 `InspectResponse` 미러 — wire 계약(server/app/models/inspection.py 와 동기). */
export type Verdict = 'OK' | 'NG'

export interface InspectResult {
  verdict: Verdict
  findings: string
  confidence: number
  timestamp: string
  image_id: string
}
