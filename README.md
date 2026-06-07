# U-Vision

> 태블릿 카메라 하나로 AI 비전 검사를 — 촬영, 판정, 불량 소견까지 자동으로.

---

## 개요

**U-Vision**은 제조 현장을 위한 범용 AI 비전 검사 서비스입니다. Progressive Web App(PWA)으로 구현되어 별도의 앱 설치 없이 브라우저만으로 구동됩니다.

검사 구역에 제품을 올려놓으면, 이동이 멈추는 순간 자동으로 촬영하고 VLM(Vision-Language Model) 서버에 전송합니다. 몇 초 안에 **OK / NG 판정과 불량 소견**이 태블릿 화면에 표시됩니다.

전용 하드웨어 불필요. 네이티브 앱 설치 불필요. 태블릿과 카메라와 브라우저면 충분합니다.

---

## 주요 기능

### 📷 정지 감지 자동 촬영
카메라는 항상 켜져 있습니다. 제품이 검사 구역에 놓이고 움직임이 멈추면 자동으로 캡처가 트리거됩니다. 작업자가 버튼을 누를 필요가 없습니다.

### 🤖 VLM 기반 OK/NG 판정
캡처된 이미지는 VLM 서버로 전송됩니다. 시나리오에 등록된 기준 이미지와 판정 기준을 참조하여 다음을 반환합니다:
- **OK / NG** 판정 결과
- 불량 소견 텍스트 (NG 시)
- 경계 케이스에 대한 신뢰도 점수

### 📋 시나리오 기반 설정
모든 제품 라인 또는 검사 유형은 **시나리오** 단위로 관리됩니다. 관리자는 셋업 화면에서 시나리오를 구성합니다:
- OK / NG 기준 이미지 등록 (다수)
- 자연어로 판정 기준 작성 (VLM 프롬프트)
- 검사 ROI(관심 영역) 지정
- 정지 감지 민감도 및 캡처 조건 설정

### 📱 PWA — 설치 불필요
U-Vision은 브라우저에서 완전히 동작합니다. 한 번 배포하면 URL 하나로 어디서든 접근 가능합니다. 앱스토어 심사나 APK 배포 없이 즉시 업데이트됩니다.

### 🔌 오프라인 대응 큐
네트워크가 불안정하거나 끊겨도 캡처 이미지는 IndexedDB에 로컬 큐잉되고, 연결이 복구되면 자동으로 업로드 및 판정이 재개됩니다.

---

## 동작 흐름

```
┌─────────────────────────────────────────────────────────┐
│                   태블릿 (PWA)                           │
│                                                         │
│  카메라 스트림 → 정지 감지 (ROI) → 멈춤 확인             │
│                                       │                 │
│                                     캡처                │
│                                       │                 │
│                                  유효성 검사             │
│                              (흔들림 / 위치 확인)         │
│                                       │                 │
│                                  업로드 큐               │
└───────────────────────────┬─────────────────────────────┘
                            │ HTTPS multipart
┌───────────────────────────▼─────────────────────────────┐
│                     서버 (API)                           │
│                                                         │
│   이미지 + 시나리오 컨텍스트 수신                         │
│        │                                                │
│        ▼                                                │
│   VLM 추론                                              │
│   (기준 이미지 + 판정 기준을 프롬프트에 포함)              │
│        │                                                │
│        ▼                                                │
│   { verdict: "NG", findings: "...", confidence: 0.91 }  │
└───────────────────────────┬─────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│               태블릿 — 결과 표시                          │
│                                                         │
│      ✅ OK  /  ❌ NG + 불량 소견 텍스트 표시              │
└─────────────────────────────────────────────────────────┘
```

---

## 화면 구성

### 운영 화면 (작업자)
- ROI 오버레이가 표시된 전체화면 카메라 뷰
- 실시간 정지 감지 상태 인디케이터
- 판정 결과: 대형 OK / NG 배지 + 불량 소견 텍스트
- 현재 세션 검사 이력 로그

### 관리자 셋업 화면
- 시나리오 목록: 생성 / 수정 / 활성화
- 기준 이미지 갤러리: OK / NG 예시 이미지 업로드
- ROI 편집기: 라이브 카메라 미리보기 위에서 검사 구역 지정
- 판정 기준 입력: VLM에 전달할 자연어 기준 작성
- 정지 감지 민감도 및 캡처 딜레이 설정
- 서버 엔드포인트 설정

---

## 기술 스택

| 레이어 | 기술 |
|---|---|
| 프론트엔드 | React (SPA) + PWA (Vite + Service Worker) |
| 정지 감지 | Canvas 픽셀 차분 — Web Worker 분리 처리 |
| 화면 유지 | Wake Lock API |
| 오프라인 저장 | IndexedDB (Dexie.js) |
| 스타일링 | Tailwind CSS |
| 서버 | ASP.NET Core (.NET 10, Minimal API) |
| VLM 백엔드 | ironhive (GPT-4o / Gemini) · 자체 서버 (vLLM, 예약) |
| 이미지 전송 | HTTPS multipart/form-data |

---

## 시나리오 구성 항목 (관리자)

**시나리오**는 하나의 검사 유형에 대한 설정 단위입니다.

| 항목 | 설명 |
|---|---|
| `name` | 시나리오 이름 (예: "PCB 상면 검사") |
| `ok_images` | 양품 기준 이미지 (권장 5~20장) |
| `ng_images` | 불량품 기준 이미지 (불량 유형별 레이블 포함) |
| `criteria` | VLM에 전달할 자연어 판정 기준 |
| `roi` | 검사 구역 픽셀 좌표 |
| `motion_threshold` | 정지 판정 기준 픽셀 차이 임계값 |
| `still_frames` | 캡처 트리거까지 연속 정지 프레임 수 |
| `min_sharpness` | 흐린 이미지 거부 기준 (라플라시안 분산 임계값) |

---

## 설치 및 실행

### 요구 사항
- HTTPS 엔드포인트 (`getUserMedia` 및 Wake Lock API 필수 조건)
- 태블릿: Android 10+ / Chrome 92+ (권장) 또는 iOS 16.4+ / Safari
- 서버: .NET 10 SDK, 자체 VLM 사용 시 GPU 권장

### 빠른 시작

```bash
# 저장소 클론
git clone https://github.com/iyulab/u-vision.git
cd u-vision

# 프론트엔드
cd client
npm install
npm run build        # dist/ 폴더에 빌드 결과 생성
npm run preview      # 로컬 HTTPS 미리보기

# 백엔드 (.NET 10)
cd ../server
dotnet run --project src/UVision.Api --urls http://0.0.0.0:8000
```

### 환경 변수

```env
# client/.env
VITE_API_BASE_URL=https://your-server.example.com

# server/.env
VLM_PROVIDER=openai          # openai | google | vllm
VLM_MODEL=gpt-4o
VLM_API_KEY=sk-...
MAX_UPLOAD_SIZE_MB=10
```

---

## API 레퍼런스

### `POST /api/inspect`

캡처 이미지를 VLM 서버에 전송하고 판정 결과를 수신합니다.

**요청** (`multipart/form-data`)

| 필드 | 타입 | 설명 |
|---|---|---|
| `image` | file | JPEG / PNG 캡처 이미지 |
| `scenario_id` | string | 활성 시나리오 식별자 |

**응답** (`application/json`)

```json
{
  "verdict": "NG",
  "findings": "좌측 하단 솔더 브릿지 검출. 3-4번 핀 단락 가능성.",
  "confidence": 0.91,
  "timestamp": "2026-06-07T09:23:41Z",
  "image_id": "img_abc123"
}
```

### `GET /api/scenarios`
등록된 시나리오 목록을 반환합니다.

### `POST /api/scenarios`
새 시나리오를 생성합니다. (기준 이미지 포함 multipart 전송)

### `GET /api/results?scenario_id=&date=`
시나리오별 검사 결과 이력을 조회합니다.

---

## 로드맵

- [ ] 라이브 카메라 위 ROI 시각 편집기
- [ ] NG 유형별 불량 카테고리 태깅
- [ ] 신뢰도 임계값 미달 시 수동 검토 큐
- [ ] 결과 내보내기 (CSV / Excel)
- [ ] 태블릿 1대 다중 ROI 지원
- [ ] 엣지 추론 모드 (온디바이스 소형 VLM)
- [ ] U-MES 검사 기록 연동

---

## 라이선스

EULA License — © 2026 iyulab

---

*U-Vision은 iyulab 제조 인텔리전스 에코시스템의 일부입니다.*