import { useEffect, useState } from 'react'

import { getHealth } from '../lib/api'
import { summarizeVolume, type VolumeSummary } from '../lib/dataVolume'

/**
 * 데이터 볼륨 여유 — 디스크가 가득 차면 판정 기록이 저장에 실패하므로(캡처는 큐에 남아 다시 간다) 차기 전에 알아채게 한다.
 * 서버가 볼륨을 읽지 못하면 아무것도 그리지 않는다. 엔진은 이 신호로 아무것도 지우지 않는다 — 보존은 설치자가 정한다.
 */
export function DataVolumePanel() {
  const [summary, setSummary] = useState<VolumeSummary | null>(null)

  useEffect(() => {
    let cancelled = false
    getHealth()
      .then((h) => !cancelled && setSummary(summarizeVolume(h)))
      .catch(() => undefined) // 상태 표시가 화면을 막지 않는다
    return () => {
      cancelled = true
    }
  }, [])

  if (!summary) return null

  return (
    <section
      className={`mb-6 rounded-xl border p-4 ${summary.low ? 'border-red-500' : 'border-slate-700'}`}
      aria-labelledby="data-volume-heading"
    >
      <h2 id="data-volume-heading" className="mb-1 text-sm font-semibold text-slate-300">
        데이터 볼륨
      </h2>
      <p className={`text-sm ${summary.low ? 'font-semibold text-red-400' : ''}`}>
        {summary.text}
        {summary.low && ' — 여유가 모자랍니다. 디스크가 차면 검사 기록 저장이 실패합니다(캡처는 큐에 남습니다).'}
      </p>
    </section>
  )
}
