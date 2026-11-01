import { chartPoints, flywheelRows, hasMlData, trendLine } from '../lib/flywheel'
import type { FlywheelReport } from '../lib/types'

const W = 300
const H = 100

/** 구간 크기 선택지 — 작을수록 점이 많고 흔들린다. */
export const FLYWHEEL_BUCKETS: readonly number[] = [10, 25, 50]

const LINES: readonly { key: 'agreement' | 'answered' | 'memory' | 'ml'; label: string; stroke: string }[] = [
  { key: 'agreement', label: '실제 판정 일치', stroke: '#cbd5e1' },
  { key: 'answered', label: '기억이 답한 비율', stroke: '#38bdf8' },
  { key: 'memory', label: '기억 답 일치', stroke: '#34d399' },
  { key: 'ml', label: 'ML 라벨 일치', stroke: '#fbbf24' },
]

/**
 * 플라이휠 곡선 — 사람이 확정한 라벨이 쌓이는 순서대로 «실제 판정» 과 «확정 사례 기억» 이 사람 라벨과 얼마나 맞았나.
 * 쓸수록 나아진다는 주장의 증거 자리다. 구간이 작으면 흔들리므로 한 줄 판정은 판단할 구간이 모자라면 말하지 않는다.
 */
export function FlywheelPanel({
  report,
  bucket,
  onBucketChange,
}: {
  report: FlywheelReport
  bucket: number
  onBucketChange: (bucket: number) => void
}) {
  const rows = flywheelRows(report.points)
  // ML 을 켜지 않은 설치는 ML 열 · 선이 없다 — 값이 없는 칸을 0 으로 채워 보이지 않는다.
  const showMl = hasMlData(report.points)
  const lines = LINES.filter((l) => l.key !== 'ml' || showMl)
  const series = {
    agreement: report.points.map((p) => p.operative_agreement),
    answered: report.points.map((p) => p.memory_answered_rate),
    memory: report.points.map((p) => p.memory_agreement),
    ml: report.points.map((p) => p.ml_agreement),
  }
  return (
    <div className="rounded-2xl border border-slate-700 bg-slate-800/50 p-5" data-testid="flywheel-panel">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-sm font-semibold text-slate-200">플라이휠 곡선</h2>
        <label className="flex items-center gap-1 text-xs text-slate-400">
          구간 크기
          <select
            aria-label="구간 크기"
            value={bucket}
            onChange={(e) => onBucketChange(Number(e.target.value))}
            className="rounded-md border border-slate-600 bg-slate-800 px-2 py-1 text-xs text-slate-200"
          >
            {FLYWHEEL_BUCKETS.map((b) => (
              <option key={b} value={b}>
                {b}건
              </option>
            ))}
          </select>
        </label>
      </div>
      <p className="text-sm text-slate-300" data-testid="flywheel-trend">
        {trendLine(report.points)}
      </p>
      <p className="mt-1 text-xs text-slate-500">
        사람이 확정한 라벨 {report.labeled}건을 쌓인 순서대로 {report.bucket_size}건씩 나눴습니다(최근 {report.days}일). 정답은 사람
        라벨뿐이고, 구간이 작으면 비율이 흔들립니다. ML 열은 전용 ML 이 라벨을 낸 설치에만 보입니다.
      </p>
      <svg role="img" aria-label="플라이휠 곡선" viewBox={`-4 -4 ${W + 8} ${H + 8}`} className="mt-3 h-28 w-full max-w-xl">
        <rect x="0" y="0" width={W} height={H} fill="none" stroke="#334155" />
        {[0.5].map((v) => (
          <line key={v} x1="0" x2={W} y1={(1 - v) * H} y2={(1 - v) * H} stroke="#334155" strokeDasharray="3 3" />
        ))}
        {lines.map((line) =>
          chartPoints(series[line.key], W, H).map((pts, i) =>
            pts.includes(' ') ? (
              <polyline key={`${line.key}-${i}`} points={pts} fill="none" stroke={line.stroke} strokeWidth="1.5" />
            ) : (
              <circle key={`${line.key}-${i}`} cx={pts.split(',')[0]} cy={pts.split(',')[1]} r="2" fill={line.stroke} />
            ),
          ),
        )}
      </svg>
      <ul className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-400">
        {lines.map((line) => (
          <li key={line.key} className="flex items-center gap-1">
            <span aria-hidden className="inline-block h-0.5 w-4" style={{ backgroundColor: line.stroke }} />
            {line.label}
          </li>
        ))}
      </ul>
      <div className="mt-4 overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead className="text-xs text-slate-400">
            <tr>
              <th className="py-1 pr-4 font-medium">누적 라벨</th>
              <th className="py-1 pr-4 font-medium">구간 판정</th>
              <th className="py-1 pr-4 font-medium">실제 판정 일치</th>
              <th className="py-1 pr-4 font-medium">불량 통과</th>
              <th className="py-1 pr-4 font-medium">기억이 답함</th>
              <th className="py-1 pr-4 font-medium">기억 답 일치</th>
              <th className="py-1 pr-4 font-medium">기억 놓침</th>
              {showMl && <th className="py-1 pr-4 font-medium">ML 라벨 일치</th>}
              {showMl && <th className="py-1 font-medium">ML 놓침</th>}
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.through} className="border-t border-slate-700 text-slate-300">
                <td className="py-1 pr-4">{r.through}</td>
                <td className="py-1 pr-4">{r.judged}</td>
                <td className="py-1 pr-4">{r.operativeAgreement}</td>
                <td className="py-1 pr-4">{r.defectPass}</td>
                <td className="py-1 pr-4">{r.memoryAnswered}</td>
                <td className="py-1 pr-4">{r.memoryAgreement}</td>
                <td className={`py-1 pr-4 ${r.memoryMissed > 0 ? 'text-amber-400' : ''}`}>{r.memoryMissed}</td>
                {showMl && <td className="py-1 pr-4">{r.mlAgreement}</td>}
                {showMl && <td className={`py-1 ${r.mlMissed > 0 ? 'text-amber-400' : ''}`}>{r.mlMissed}</td>}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
