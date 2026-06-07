import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import basicSsl from '@vitejs/plugin-basic-ssl'
import { VitePWA } from 'vite-plugin-pwa'

// getUserMedia / Wake Lock 은 secure context 필수.
// localhost 는 http 라도 secure 로 취급되지만, 실디바이스(태블릿) IP 접근 테스트를 위해
// basic-ssl 로 dev/preview 에 self-signed HTTPS 를 제공한다.
export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    basicSsl(),
    VitePWA({
      registerType: 'autoUpdate',
      manifest: {
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
  server: {
    host: true,
  },
})
