import { useState } from 'react'

import { resultImageUrl } from '../lib/api'
import { boxStyles, countSummary } from '../lib/countBoxes'
import type { CountInfo } from '../lib/types'

/**
 * 계수 검사의 캡처 이미지 — 센 상자를 원본 위에 겹쳐 «왜 이 개수인가» 를 사진에서 확인하게 한다. 문턱 근처 상자(넣고 빼는 데
 * 따라 판정이 바뀔 수 있는 것)는 다른 색으로 구분한다. 좌표가 이미지 비율이라 이미지 크기를 기다리지 않는다.
 */
export function CountImage({
  scenarioId,
  date,
  imageId,
  count,
}: {
  scenarioId: string
  date: string
  imageId: string
  count: CountInfo
}) {
  const [show, setShow] = useState(true)
  const boxes = boxStyles(count)
  const near = boxes.filter((b) => b.near).length

  return (
    <div className="space-y-2">
      <div className="relative">
        <img src={resultImageUrl(scenarioId, date, imageId)} alt="검사 캡처" className="w-full rounded-lg bg-black object-contain" />
        {show && (
          <div className="pointer-events-none absolute inset-0" data-testid="count-layer">
            {boxes.map((b, i) => (
              <div
                key={i}
                data-near={b.near}
                title={`${b.label} ${(b.confidence * 100).toFixed(0)}%`}
                className={`absolute border-2 ${b.near ? 'border-amber-400' : 'border-emerald-400'}`}
                style={{ left: `${b.left}%`, top: `${b.top}%`, width: `${b.width}%`, height: `${b.height}%` }}
              />
            ))}
          </div>
        )}
      </div>

      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-slate-400">
        <button
          type="button"
          onClick={() => setShow((s) => !s)}
          aria-pressed={show}
          className={`rounded-lg px-2.5 py-1 font-medium ${
            show ? 'bg-emerald-600/80 text-white' : 'bg-slate-700 text-slate-200 hover:bg-slate-600'
          }`}
        >
          상자 {show ? '끄기' : '보기'}
        </button>
        <span className="text-slate-200">{countSummary(count)}</span>
        <span>신뢰도 {count.min_confidence} 이상만 센다</span>
        {near > 0 && <span className="text-amber-300">노란 상자 {near}개는 문턱 근처 — 넣고 빼는 데 따라 판정이 바뀔 수 있다</span>}
        {count.uncertain && <span className="text-amber-300">흔들리는 판정이라 NG · 사람 확인 대상</span>}
      </div>
    </div>
  )
}
