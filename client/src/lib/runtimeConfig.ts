/** 호스트 미들웨어가 index.html에 주입하는 런타임 설정(window.__UVISION_CONFIG__). */
export interface UVisionRuntimeConfig {
  apiBase: string
  basePath: string
  title: string
}

declare global {
  interface Window {
    __UVISION_CONFIG__?: UVisionRuntimeConfig
  }
}

/** apiBase 해석 우선순위: 런타임 주입 > 빌드 env > 기본(/api/u-vision). */
export function resolveApiBase(): string {
  return (
    window.__UVISION_CONFIG__?.apiBase ??
    import.meta.env.VITE_API_BASE_URL ??
    '/api/u-vision'
  )
}
