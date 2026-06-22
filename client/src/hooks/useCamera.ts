import { useEffect, useRef, useState, type RefObject } from 'react'

export interface CameraState {
  videoRef: RefObject<HTMLVideoElement | null>
  ready: boolean
  error: string | null
}

/**
 * 후면 카메라 스트림을 video 엘리먼트에 연결한다.
 *
 * secure context(HTTPS/localhost) 필수. iOS Safari 는 라우트 이동 시 권한을
 * 재요청하는 버그가 있으므로(아키텍처 원칙: 단일뷰 SPA) 이 훅은 앱 수명 동안
 * 한 번만 마운트되는 것을 전제로 한다.
 */
export function useCamera(): CameraState {
  const videoRef = useRef<HTMLVideoElement | null>(null)
  const [ready, setReady] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let stream: MediaStream | null = null
    let cancelled = false

    async function start() {
      try {
        stream = await navigator.mediaDevices.getUserMedia({
          video: {
            facingMode: { ideal: 'environment' },
            width: { ideal: 1280 },
            height: { ideal: 720 },
          },
          audio: false,
        })
        if (cancelled) {
          stream.getTracks().forEach((t) => t.stop())
          return
        }
        const video = videoRef.current
        if (video) {
          video.srcObject = stream
          await video.play()
          setReady(true)
        }
      } catch (e) {
        setError(e instanceof Error ? e.message : '카메라 접근 실패')
      }
    }

    void start()
    return () => {
      cancelled = true
      stream?.getTracks().forEach((t) => t.stop())
    }
  }, [])

  return { videoRef, ready, error }
}
