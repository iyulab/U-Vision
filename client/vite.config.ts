/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import basicSsl from '@vitejs/plugin-basic-ssl'
import { VitePWA } from 'vite-plugin-pwa'

// getUserMedia / Wake Lock 은 secure context 필수.
// localhost 는 http 라도 secure 로 취급되지만, 실디바이스(태블릿) IP 접근 테스트를 위해
// basic-ssl 로 dev/preview 에 self-signed HTTPS 를 제공한다.
const basePath = process.env.VITE_BASE ?? '/'

export default defineConfig({
  base: basePath,
  plugins: [
    react(),
    tailwindcss(),
    basicSsl(),
    VitePWA({
      registerType: 'autoUpdate',
      scope: basePath,
      manifest: {
        id: basePath,
        start_url: basePath,
        scope: basePath,
        name: 'U-Vision',
        short_name: 'U-Vision',
        description: '제조 현장용 AI 비전 검사',
        theme_color: '#0f172a',
        background_color: '#0f172a',
        display: 'standalone',
        orientation: 'landscape',
        icons: [
          { src: 'icon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'any' },
          { src: 'icon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'maskable' },
        ],
      },
      includeAssets: ['icon.svg'],
      // dev 에서 SW 비활성 — 카메라/디버깅 방해 방지. 프로덕션 빌드에서만 SW.
      devOptions: { enabled: false },
    }),
  ],
  build: {
    rollupOptions: {
      output: {
        entryFileNames: 'assets/[name].js',
        chunkFileNames: 'assets/[name].js',
        assetFileNames: 'assets/[name][extname]',
      },
    },
  },
  server: {
    host: true,
  },
  // 단위 테스트는 localStorage/crypto 등 브라우저 API 를 쓰는 순수 로직(deviceIdentity·captureMode)을
  // 포함하므로 DOM 환경이 필요하다. happy-dom 은 jsdom 보다 가벼운 표준 선택.
  test: {
    environment: 'happy-dom',
  },
})
