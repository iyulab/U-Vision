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
- **오투입 확인** — 작업자가 기대 품번을 넣고 촬영하면 기대 품번과 일치/불일치로 판정. 기기가 바코드를 읽을 수 있으면 읽은 코드와 먼저 대조하고, 비전 모델은 기대 품번을 들고 확인한다
- **끊겨도 잃지 않는 촬영** — 캡처를 기기에 먼저 저장하고 서버에 닿지 못하면 연결이 돌아오는 대로 다시 보낸다. 재전송은 `Idempotency-Key` 로 한 번만 판정 · 기록되고, 늦게 나온 NG 는 작업자가 확인할 때까지 화면에 남는다
- **MES · 외부 시스템 연동** — 판정마다 CloudEvents 사건을 웹훅으로(서명 · 재시도 · 상태 보기), API 계약은 OpenAPI 문서로 공개
- **사람 라벨링 → 학습 데이터** — 결과에 OK/NG 라벨을 달아 ground-truth 축적, 전용 ML 학습입력으로 export
- **온프레미스·주권** — GPUStack(셀프호스트 VLM)·MLoop(로컬 ML)로 외부 egress 0 경로. 데이터가 공장을 떠나지 않는다
- **PWA — 설치 불필요** — URL 하나로 어디서든, 즉시 업데이트

## 로드맵

아직 지어지지 않은 방향이다. 순서는 현장 요구로 정한다.

- **고정 카메라 영상 시나리오** — 정지 사진 검사를 넘어 라인 위 고정 카메라 영상에서 판정한다: 수량 세기(통과선), 안전모 착용 감시, 화재 · 연기 조기경보, 정상 사진만으로 시작하는 이상 감지. 판정 결과의 모양과 지표는 시나리오 유형이 정한다.
- **엣지 장치** — 영상은 라인 옆 엣지 장치(Jetson Orin)가 처리하고, 서버로는 판정할 사건과 잘라 낸 사진만 보낸다. 연결이 끊기면 장치에 쌓았다가 다시 보낸다.
- **확정 사례 기억** — 사람이 확정한 사례를 기억해 비슷한 건은 모델 학습 없이 바로 판정하고, 애매한 건만 위 단계로 올린다.
- **검증된 권한 이양** — 아래 단계 판정자가 확정한 건 가운데 일부를 눈가림으로 다시 판정해, 미리 정한 기준을 통과한 만큼만 판정을 넘긴다.
- **현장 시스템 연동 확장** — MES 에서 작업지시 · 로트 · 기대 품번을 받아 오투입 확인에 쓰고, 결과를 MQTT · OPC UA 로도 내보낸다.

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
- `POST /api/u-vision/v2/inspect` — 같은 판정을 시나리오 클래스 · 판정을 낸 단 · 판정 경로로(오투입 확인은 `expected_part` · `observed_code` 를 함께)
- `GET /api/u-vision/scenarios` · `GET /api/u-vision/results?scenario_id=&date=`
- 변경(생성/수정/삭제·업로드·export)은 `X-Admin-Pin` 헤더 필요
- 전체 계약: OpenAPI [`server/openapi/u-vision.json`](./server/openapi/u-vision.json) (단독 서버는 `/openapi/v1.json`)

SDK 임베드 가이드는 [`server/src/UVision.Hosting/README.md`](./server/src/UVision.Hosting/README.md) 참고.

## 라이선스

[AGPL-3.0-or-later](./LICENSE) — © 2026 iyulab. AGPL 조건으로 쓰기 어려운 조직에는 상용 라이선스를 드립니다. 공장 안 서버에 구축하고 유지보수하는 일도 iyulab 이 맡습니다 — 문의는 [도입 상담](https://vision.u-platform.kr/).

- 0.4.0 부터 AGPL 이다. 이미 게시된 0.3.0 이하의 `UVision.Hosting` 패키지는 MIT 그대로 남는다.
- 다른 시스템과는 별도 앱으로 두고 HTTP API · 웹훅으로 연동하는 것이 기본이다. 네트워크 너머에서 부르는 쪽에는 AGPL 이 미치지 않는다.
- 기여에는 [CLA](./CLA.md) 서명이 필요하다 — [CONTRIBUTING](./CONTRIBUTING.md).
- 화면의 U-Platform 소속 표시는 [`@uplatform/brand`](https://www.npmjs.com/package/@uplatform/brand) 패키지를 쓴다. 코드는 MIT, 마크와 제품명은 iyulab 의 상표로 이 리포의 라이선스 밖이다.
