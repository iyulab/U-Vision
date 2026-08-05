import { useState } from 'react'

import { ApiError, setupAdmin } from '../lib/api'

/**
 * 관리자 PIN 최초 설정 화면(first-run bootstrap) — 신규 설치에서 서버 파일 수정·재시작 없이 UI 만으로
 * 첫 PIN 을 정한다.
 *
 * 서버는 PIN 미설정 시 데이터 폴더에 일회용 <c>admin-setup-token.txt</c> 를 생성한다(파일시스템 접근 =
 * 신뢰 루트, 네트워크상 선점 탈취 방지). 서버 머신에 접근 가능한 관리자가 그 토큰과 새 PIN 을 입력하면
 * 부트스트랩이 완료된다.
 */
export function AdminSetup({ onDone }: { onDone: () => void }) {
  const [token, setToken] = useState('')
  const [pin, setPin] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const pinMismatch = confirm.length > 0 && pin !== confirm
  const canSubmit =
    token.trim().length > 0 && pin.trim().length >= 4 && pin === confirm && !busy

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!canSubmit) return
    setBusy(true)
    setError(null)
    try {
      await setupAdmin(token.trim(), pin.trim())
      onDone()
    } catch (err) {
      setError(messageFor(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-screen w-full items-center justify-center bg-slate-900 p-6 text-slate-100">
      <form onSubmit={submit} className="w-full max-w-md space-y-5 rounded-2xl bg-slate-800 p-8 shadow-xl">
        <div className="space-y-2">
          <h1 className="text-xl font-semibold text-white">관리자 PIN 최초 설정</h1>
          <p className="text-sm text-slate-400">
            이 서버에는 아직 관리자 PIN 이 없습니다. 서버 데이터 폴더의{' '}
            <code className="rounded bg-slate-900 px-1.5 py-0.5 text-emerald-400">admin-setup-token.txt</code>{' '}
            파일 내용을 입력하고 새 PIN 을 정하세요.
          </p>
        </div>

        <label className="block space-y-1">
          <span className="text-sm text-slate-300">setup-token</span>
          <input
            value={token}
            onChange={(e) => setToken(e.target.value)}
            autoFocus
            autoComplete="off"
            placeholder="admin-setup-token.txt 내용 붙여넣기"
            className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 font-mono text-sm text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none"
          />
        </label>

        <label className="block space-y-1">
          <span className="text-sm text-slate-300">새 PIN (4자리 이상)</span>
          <input
            type="password"
            inputMode="numeric"
            value={pin}
            onChange={(e) => setPin(e.target.value)}
            placeholder="새 PIN"
            className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none"
          />
        </label>

        <label className="block space-y-1">
          <span className="text-sm text-slate-300">새 PIN 확인</span>
          <input
            type="password"
            inputMode="numeric"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            placeholder="새 PIN 다시 입력"
            className="w-full rounded-lg border border-slate-600 bg-slate-900 px-3 py-2 text-white placeholder-slate-500 focus:border-emerald-400 focus:outline-none"
          />
          {pinMismatch && <span className="text-xs text-red-400">PIN 이 일치하지 않습니다.</span>}
        </label>

        {error && <div className="rounded-lg bg-red-600/90 px-4 py-2 text-sm">{error}</div>}

        <button
          type="submit"
          disabled={!canSubmit}
          className="w-full rounded-lg bg-emerald-500 px-4 py-2.5 text-sm font-semibold text-white hover:bg-emerald-400 disabled:opacity-40"
        >
          {busy ? '설정 중…' : 'PIN 설정'}
        </button>

        <p className="text-xs text-slate-500">
          PIN 을 잊으면 서버에서 <code className="text-slate-400">admin-pin.json</code> 을 삭제하고 재시작하면
          다시 설정할 수 있습니다.
        </p>
      </form>
    </div>
  )
}

/** ApiError 상태코드를 사용자 안내로 변환한다. */
function messageFor(err: unknown): string {
  if (err instanceof ApiError) {
    switch (err.status) {
      case 401:
        return 'setup-token 이 올바르지 않습니다. 서버 데이터 폴더의 파일 내용을 다시 확인하세요.'
      case 400:
        return '새 PIN 은 공백 없이 4자리 이상이어야 합니다.'
      case 409:
        return '이미 PIN 이 설정되었거나 진행 중인 설정이 없습니다. 페이지를 새로고침하세요.'
      case 410:
        return '이 서버는 설정 파일로 PIN 이 지정되어 최초 설정이 비활성입니다.'
    }
  }
  return err instanceof Error ? err.message : '최초 설정 실패'
}
