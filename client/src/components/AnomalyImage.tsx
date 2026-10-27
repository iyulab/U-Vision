import { useState } from 'react'

import { useResource } from '../hooks/useResource'
import { getAnomalyMap, resultImageUrl } from '../lib/api'
import { heatCells, hottestCells } from '../lib/anomalyMap'

const FAIL = '이상 지도를 읽지 못했습니다'

/**
 * 검사 캡처 이미지 — 그 검사에 이상 지도(서버가 패치 이상 점수를 쌓을 때 남기는 관찰 자료)가 있으면 «열지도» 단추로 원본 위에
 * 패치 격자의 색을 겹쳐 보인다. 지도가 없거나 못 읽으면 단추도 없이 이미지만 — 이전 화면과 같다. 판정과 무관한 보조 근거다.
 */
export function AnomalyImage({ scenarioId, date, imageId }: { scenarioId: string; date: string; imageId: string }) {
  const map = useResource(`${scenarioId}/${date}/${imageId}`, () => getAnomalyMap(scenarioId, date, imageId), FAIL)
  const [show, setShow] = useState(false)
  const [size, setSize] = useState<{ width: number; height: number } | null>(null)

  const anomaly = map.data ?? null
  const cells = anomaly !== null && size !== null ? heatCells(anomaly, size.width, size.height) : []

  return (
    <div className="space-y-2">
      <div className="relative">
        <img
          src={resultImageUrl(scenarioId, date, imageId)}
          alt="검사 캡처"
          className="w-full rounded-lg bg-black object-contain"
          onLoad={(e) =>
            setSize({ width: e.currentTarget.naturalWidth, height: e.currentTarget.naturalHeight })
          }
        />
        {show && cells.length > 0 && (
          <div className="pointer-events-none absolute inset-0" data-testid="heat-layer">
            {cells.map((c, i) => (
              <div
                key={i}
                className="absolute"
                style={{
                  left: `${c.left}%`,
                  top: `${c.top}%`,
                  width: `${c.width}%`,
                  height: `${c.height}%`,
                  backgroundColor: `rgba(239, 68, 68, ${(c.intensity * 0.6).toFixed(3)})`,
                }}
              />
            ))}
          </div>
        )}
      </div>

      {anomaly !== null && (
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-slate-400">
          <button
            type="button"
            onClick={() => setShow((s) => !s)}
            aria-pressed={show}
            className={`rounded-lg px-2.5 py-1 font-medium ${
              show ? 'bg-red-600/80 text-white' : 'bg-slate-700 text-slate-200 hover:bg-slate-600'
            }`}
          >
            열지도 {show ? '끄기' : '보기'}
          </button>
          <span>
            패치 점수 <span className="font-mono text-slate-200">{anomaly.patch_score.toFixed(3)}</span> · 전역{' '}
            <span className="font-mono text-slate-200">{anomaly.global_score.toFixed(3)}</span> · 정상 기억{' '}
            {anomaly.bank_size}장
          </span>
          {show && (
            <span>
              색은 이 사진 안의 상대값(가장 먼 자리가 가장 진함) · 가장 먼 칸 (행, 열):{' '}
              {hottestCells(anomaly, 3)
                .map((h) => `(${h.row + 1}, ${h.column + 1})`)
                .join(' ')}
            </span>
          )}
        </div>
      )}
    </div>
  )
}
