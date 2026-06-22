# U-Vision

> 태블릿 카메라 하나로 시작하는 AI 비전 검사 — 첫날은 VLM 이 선별하고, 시간이 갈수록 전용 ML 이 판정을 인수한다.

**U-Vision**은 제조 현장을 위한 범용 AI 비전 검사 PWA다. 별도 앱 설치 없이 브라우저만으로 구동되며, 단일 조직 셀프호스트를 전제한다. 검사 구역에 제품을 올려놓으면 이동이 멈추는 순간 자동으로 촬영하고, 서버가 시나리오 기준으로 **OK / NG**(또는 다중분류 등급)와 불량 소견을 몇 초 안에 표시한다.

## 신뢰성 진화 플라이휠

제조는 품질 0.1% 에 사활을 거는 산업이다. 이런 정량 영역에서 VLM 의 할루시네이션은 위험하므로, U-Vision 은 VLM 을 **판정자가 아니라 1차 선별기(트리아지)** 로 쓴다.

```
 ① VLM 으로 즉시 시작 (데이터 0, 첫날부터 — 1차 선별, 확정은 사람)
        ▼  운영하며 라벨 데이터 축적
 ② 라벨 데이터셋 → 전용 ML 비전 모델 빌드
        ▼
 ③ VLM + ML 2중 체크 → 신뢰성 점수화
        ▼  불일치 시
 ④ 오라클 / 사람 에스컬레이션
        ▼
 ⑤ 자가강화: 재학습 · A/B · 점진 권한 이양  ──↺ 다시 ①로
```

> **정직한 경계(현재 구현):** 현재 VLM 이 OK/NG verdict 를 직접 산출(주 판정)하고, 전용 ML 이 교차검증(additive·기본 비활성)으로 결선되어 불일치/저신뢰를 검토 큐로 표면화한다(불일치는 운영 화면에 비차단 '검토 필요'로 알리되 라인은 멈추지 않는 **advisory·NG-safe**; 주 검출원 VLM 이 사용 불가하면 자동 판정 대신 '판정 불가 — 사람 확인'으로 **fail-closed**). 사람 라벨링은 선택적 ground-truth 수집이다. 실 ML serve 통합은 검증됐다(mloop `serve /predict` 왕복 e2e). VLM→트리아지 격하·ML 판정 권한 이양은 데이터 근거로 진행될 진화 단계다(④~⑤).

## 주요 기능

- **정지 감지 자동 촬영** — 카메라는 항상 켜져 있고 제품이 멈추면 자동 캡처, 버튼 불필요
- **VLM 1차 선별 + 전용 ML 교차검증** — 기준 이미지(few-shot)·자연어 기준으로 OK/NG·소견·신뢰도. ML 병렬 2중체크로 불일치/저신뢰를 검토 큐로 표면화
- **fail-safe 운영 자세** — 불일치는 운영 화면에 비차단 '검토 필요'(라인 유지·NG-safe), 검출원(VLM) 장애 시 자동 판정 대신 '판정 불가 — 사람 확인'(fail-closed). 발동률은 메트릭 대시보드로 관측
- **시나리오 기반 설정** — 라인·검사 유형별로 기준 이미지·자연어 기준·ROI·정지 민감도·latency 레버 관리
- **사람 라벨링 → 학습 데이터** — 결과에 OK/NG 라벨을 달아 ground-truth 축적, 전용 ML 학습입력으로 export
- **온프레미스·주권** — GPUStack(셀프호스트 VLM)·MLoop(로컬 ML)로 외부 egress 0 경로. 데이터가 공장을 떠나지 않는다
- **PWA — 설치 불필요** — URL 하나로 어디서든, 즉시 업데이트

## 기술 스택

| 레이어 | 기술 |
|---|---|
| 프론트엔드 | React 19 (SPA) + PWA (Vite 8 + Service Worker) + Tailwind v4 |
| 정지 감지 | OffscreenCanvas 픽셀 차분 — Web Worker 분리 |
| 서버 | ASP.NET Core (.NET 10, Minimal API) |
| VLM 백엔드 | ironhive(openai/google) · GPUStack(셀프호스트) · vLLM(예약) |
| 전용 ML | MLoop image-classification(`mloop serve` HTTP 경계) |
| 저장소 | 순수 파일시스템 (DB 없음) |

## 빠른 시작

```bash
git clone https://github.com/iyulab/U-Vision.git
cd U-Vision

# 프론트엔드
cd client && npm install && npm run build   # dist/ 생성

# 백엔드 (.NET 10) — 기본 mock provider 라 키 없이 기동
cd ../server
dotnet run --project src/UVision.Api --urls http://0.0.0.0:8000
```

기본 VLM provider 는 `mock` 이라 API 키 없이도 서버·테스트가 전부 동작한다. 셀프호스트 VLM 사용 시 GPU 를 권장한다.

### 핵심 환경 변수

```env
# client/.env — 값은 반드시 /api/u-vision 네임스페이스를 포함한다.
VITE_API_BASE_URL=https://your-server.example.com/api/u-vision

# server/.env
VLM_PROVIDER=mock        # mock | openai | google | gpustack | vllm
ML_PROVIDER=none         # none | mock | mloop
ADMIN_PIN=               # 미설정 시 관리 엔드포인트 503 (운영은 정상)
```

## API

엔드포인트는 `/api/u-vision` 네임스페이스 아래에 있다.

- `POST /api/u-vision/inspect` — 캡처 이미지 판정(multipart: `image`·`scenario_id`)
- `GET /api/u-vision/scenarios` · `GET /api/u-vision/results?scenario_id=&date=`
- 변경(생성/수정/삭제·업로드·export)은 `X-Admin-Pin` 헤더 필요

SDK 임베드 가이드는 [`server/src/UVision.Hosting/README.md`](./server/src/UVision.Hosting/README.md) 참고.

## 라이선스

[MIT License](./LICENSE) — © 2026 iyulab
