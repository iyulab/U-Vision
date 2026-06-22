import { useEffect, useRef, useState } from 'react'

import { deleteReference, listReferences, referenceUrl, uploadReference } from '../lib/api'
import type { PinRunResult } from '../hooks/usePin'
import type { Reference } from '../lib/types'

interface ReferenceGalleryProps {
  scenarioId: string
  /** 변경(업로드·삭제)을 PIN 과 함께 실행하는 중앙 러너(콘솔 소유). */
  runWithPin: (action: (pin: string) => Promise<void>) => Promise<PinRunResult>
}

/**
 * 기준 이미지 갤러리(S-D) — OK/NG 기준 이미지 업로드·미리보기·삭제.
 * few-shot 판정 결합은 서버가 inspect 시 수행한다(판정 효과는 M0.1 게이트).
 * 변경은 시나리오 저장/삭제와 동일한 lazy-PIN 메커니즘({@link runWithPin})을 거친다.
 */
export function ReferenceGallery({ scenarioId, runWithPin }: ReferenceGalleryProps) {
  const [refs, setRefs] = useState<Reference[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function reload() {
    try {
      setRefs(await listReferences(scenarioId))
    } catch (e) {
      setError(e instanceof Error ? e.message : '목록 로드 실패')
    }
  }

  useEffect(() => {
    void reload()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scenarioId])

  async function mutate(action: (pin: string) => Promise<void>) {
    setBusy(true)
    setError(null)
    try {
      const result = await runWithPin(action)
      if (result === 'ok') await reload()
      else if (result === 'unauthorized') setError('PIN 이 올바르지 않습니다. 다시 시도하세요.')
      // 'cancelled' → 조용히 무시
    } catch (e) {
      setError(e instanceof Error ? e.message : '요청 실패')
    } finally {
      setBusy(false)
    }
  }

  const ok = refs.filter((r) => r.label === 'ok')
  const ng = refs.filter((r) => r.label === 'ng')

  return (
    <div className="space-y-4 rounded-lg border border-slate-700 p-4">
      <h3 className="text-sm font-semibold text-slate-200">기준 이미지</h3>
      {error && <p className="text-sm text-red-400">{error}</p>}

      <Group
        title="OK 기준"
        label="ok"
        items={ok}
        scenarioId={scenarioId}
        busy={busy}
        onUpload={(file) => mutate((pin) => uploadReference(scenarioId, file, 'ok', undefined, pin).then(() => {}))}
        onDelete={(refId) => mutate((pin) => deleteReference(scenarioId, 'ok', refId, pin))}
      />
      <Group
        title="NG 기준"
        label="ng"
        items={ng}
        scenarioId={scenarioId}
        busy={busy}
        withLabel
        onUpload={(file, ngLabel) =>
          mutate((pin) => uploadReference(scenarioId, file, 'ng', ngLabel, pin).then(() => {}))
        }
        onDelete={(refId) => mutate((pin) => deleteReference(scenarioId, 'ng', refId, pin))}
      />
    </div>
  )
}

function Group({
  title,
  label,
  items,
  scenarioId,
  busy,
  withLabel = false,
  onUpload,
  onDelete,
}: {
  title: string
  label: 'ok' | 'ng'
  items: Reference[]
  scenarioId: string
  busy: boolean
  withLabel?: boolean
  onUpload: (file: File, ngLabel?: string) => void
  onDelete: (refId: string) => void
}) {
  const fileRef = useRef<HTMLInputElement>(null)
  const [ngLabel, setNgLabel] = useState('')

  function pick(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    if (file) onUpload(file, withLabel ? ngLabel.trim() || undefined : undefined)
    e.target.value = '' // 같은 파일 재선택 허용
    setNgLabel('')
  }

  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2">
        <span className="text-xs font-medium text-slate-400">{title}</span>
        {withLabel && (
          <input
            value={ngLabel}
            onChange={(e) => setNgLabel(e.target.value)}
            placeholder="불량 유형(선택)"
            className="rounded border border-slate-600 bg-slate-900 px-2 py-1 text-xs text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none"
          />
        )}
        <button
          type="button"
          disabled={busy}
          onClick={() => fileRef.current?.click()}
          className="rounded bg-slate-700 px-2 py-1 text-xs text-slate-200 hover:bg-slate-600 disabled:opacity-40"
        >
          + 추가
        </button>
        <input
          ref={fileRef}
          type="file"
          accept="image/jpeg,image/png"
          className="hidden"
          onChange={pick}
        />
      </div>

      {items.length === 0 ? (
        <p className="text-xs text-slate-500">없음</p>
      ) : (
        <div className="flex flex-wrap gap-2">
          {items.map((r) => (
            <div key={r.ref_id} className="group relative">
              <img
                src={referenceUrl(scenarioId, label, r.ref_id)}
                alt={r.ng_label ?? r.ref_id}
                className="h-16 w-16 rounded object-cover"
              />
              {r.ng_label && (
                <span className="absolute inset-x-0 bottom-0 truncate rounded-b bg-black/70 px-1 text-[10px] text-white">
                  {r.ng_label}
                </span>
              )}
              <button
                type="button"
                onClick={() => onDelete(r.ref_id)}
                className="absolute -right-1 -top-1 hidden h-5 w-5 rounded-full bg-red-600 text-xs text-white group-hover:block"
                aria-label="삭제"
              >
                ×
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
