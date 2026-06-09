# UVision.Hosting

제조 현장용 **AI 비전 검사 PWA + API**를 ASP.NET Core 호스트에 임베드하는 라이브러리. 태블릿 카메라가 정지 감지로 자동 촬영 → VLM이 시나리오 기준으로 OK/NG 판정. 빌드된 PWA가 패키지(어셈블리)에 임베드되어 있어 **별도 정적 파일 배포가 필요 없다**.

- **PWA** 가 `BasePath`(기본 `/u-vision`)에 마운트
- **API** 가 `ApiBasePath`(기본 `/api/u-vision`)에 마운트
- 기본 VLM provider 는 `mock` → **API 키 없이 바로 동작**(연동 확인용)
- 단일 조직 셀프호스트 전제(멀티테넌시·계정 시스템 없음)

> 패턴: `AddUVision()` + `UseUVision()` + `MapUVisionEndpoints()`. vault-ai(`Iyu.VaultAi.v3.0`)와 동일한 임베드-SPA 통합 모델.

## 설치

```bash
dotnet add package UVision.Hosting
```

## 최소 통합 (3줄 와이어링)

`Program.cs`:

```csharp
using UVision.Api;
using UVision.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// 1) 코어 서비스 등록 — 설정 섹션 "UVision"에서 바인딩, 스토리지를 contentRoot 아래로 절대화.
builder.Services.AddUVision(builder.Configuration, builder.Environment.ContentRootPath);

var app = builder.Build();

// 2) PWA(BasePath) + 정적 에셋 + 런타임 config 주입.  ※ 호스트의 라우팅/엔드포인트보다 먼저 둔다.
app.UseUVision();

// 3) API(ApiBasePath) 엔드포인트 매핑.
app.MapUVisionEndpoints(app.Services.GetRequiredService<UVisionOptions>().ApiBasePath);

app.Run();
```

설정 없이도 동작한다(기본값: provider `mock`, 스토리지 `./data`, PWA `/u-vision`, API `/api/u-vision`). 브라우저로 `https://<host>/u-vision/` 접속 → PWA 로드. `GET /api/u-vision/health` → `{"status":"ok","provider":"mock"}`.

> `getUserMedia`(카메라)·Wake Lock 은 **secure context** 필수 — 운영 시 HTTPS(또는 `localhost`)로 서빙한다.

전체 동작 예제: [`samples/MinimalHost`](https://github.com/iyulab/U-Vision/tree/main/samples/MinimalHost).

## 설정 (config 섹션 `UVision`)

`appsettings.json`:

```jsonc
{
  "UVision": {
    "Vlm": {
      "Provider": "mock",      // mock | openai | google | gpustack
      "Model": "gpt-4o",
      "ApiKey": "",            // openai/google
      "Endpoint": "",          // gpustack 등 OpenAI 호환 self-host base URL
      "MaxUploadSizeMb": 10
    },
    "Storage": { "DataPath": "data" },   // 상대경로는 호스트 ContentRoot 기준
    "AdminPin": null,                     // 미설정 시 관리(쓰기) 엔드포인트는 503, 운영(읽기)은 정상
    "BasePath": "/u-vision",              // PWA 마운트 경로 (⚠️ 아래 제약 참고)
    "ApiBasePath": "/api/u-vision",       // API 네임스페이스
    "Title": "U-Vision"                   // 런타임 config로 SPA에 주입
  }
}
```

실제 VLM provider 연동 예(GPUStack, OpenAI 호환 self-host):

```jsonc
"Vlm": { "Provider": "gpustack", "Model": "qwen2.5-vl", "Endpoint": "http://gpu-host:8080" }
```

## 인증 모델

- **운영/읽기 무인증**: `GET /api/u-vision/scenarios`, 결과·기준이미지 조회·서빙, `POST /api/u-vision/inspect`.
- **관리/쓰기는 PIN**: 시나리오 생성·수정·삭제, 기준이미지 업로드·삭제는 헤더 `X-Admin-Pin` 필요.
- `UVision:AdminPin` 미설정 → 관리 엔드포인트는 **503**(운영은 정상). 계정·세션·역할 시스템은 의도적으로 없다.

## API 표면 (`{ApiBasePath}` 하위, 기본 `/api/u-vision`)

| 메서드 | 경로 | 인증 |
|---|---|---|
| GET | `/health` | - |
| POST | `/inspect` (multipart) | - |
| GET | `/results`, `/results/dates`, `/results/image` | - |
| GET | `/scenarios`, `/scenarios/{id}` | - |
| POST/PUT/DELETE | `/scenarios...` | PIN |
| GET | `/scenarios/{id}/references...` | - |
| POST/DELETE | `/scenarios/{id}/references...` | PIN |

JSON wire 는 snake_case(`image_id`, `scenario_id`, `ng_label`), `verdict` 는 `"OK"`/`"NG"`. 이 계약은 DTO `[JsonPropertyName]`이 보유하므로 **호스트의 전역 JSON 정책과 무관**(호스트 JSON casing 을 오염시키지 않는다).

## 미들웨어 배치

`UseUVision()`은 `{BasePath}/**`(GET/HEAD)와 `{ApiBasePath}/**`만 처리하고 나머지는 통과시킨다(`/u-vision-admin` 같은 형제 경로도 삼키지 않음). 호스트의 catch-all/SPA fallback 보다 **앞에** 두면 안전하다. CORS 는 호스트 책임 — PWA 를 같은 호스트에서 서빙하면 불필요하다.

## ⚠️ 제약: `BasePath` 는 클라이언트 빌드와 결합

`BasePath` 기본값 `/u-vision` 은 임베드된 PWA 빌드의 base 와 일치한다. **다른 경로로 마운트하려면 클라이언트를 그 경로의 `VITE_BASE` 로 재빌드해야 한다** — service worker scope·manifest·번들 에셋이 빌드타임 base 를 baked 하기 때문이다(미들웨어는 `index.html` 만 best-effort 치환). 기본 `/u-vision` 사용을 권장한다.

## 스토리지

순수 파일시스템(DB 없음). `{ContentRoot}/{UVision:Storage:DataPath}/{scenario}/...` 아래에 시나리오 정의·기준 이미지·캡처·판정 결과가 누적된다. 단일 조직 셀프호스트의 진실의 원천.

---

소스·이슈: https://github.com/iyulab/U-Vision · © 2026 iyulab.
