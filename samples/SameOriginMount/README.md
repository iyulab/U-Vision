# SameOriginMount — 업무 앱의 출처 아래에 U-Vision 화면을 싣는 리버스 프록시 예제

U-Vision 을 **별도 엔진 서버**로 띄우고, 업무 앱(MES · 포털 등) 앞의 리버스 프록시가 `/u-vision/`(화면)과 `/api/u-vision/`(API)만 엔진으로 넘긴다. 작업자에게는 업무 앱과 **같은 주소(출처)** 의 한 화면으로 보이고, 업무 앱 프로세스 안에 U-Vision 을 넣지 않는다.

```bash
cd samples/SameOriginMount
docker compose up --build
# → http://localhost:8200/            업무 앱 자리(예제에선 글 한 줄)
# → http://localhost:8200/u-vision/   U-Vision 화면 — 업무 앱과 같은 출처
```

## 왜 같은 출처인가

- 화면이 부르는 API 주소가 상대 경로(`/api/u-vision`)라 CORS 가 필요 없다.
- 카메라 권한 · 설치(PWA) · 오프라인 큐가 업무 앱 주소에 묶여, 작업자가 다른 주소를 따로 기억하지 않는다.
- 엔진 포트는 밖에 열지 않는다 — 프록시만 엔진에 닿는다(`compose.yaml` 의 `engine` 에 `ports` 없음).

## 프록시 규칙 (`nginx.conf`)

- **경로를 그대로 넘긴다.** 엔진이 이미 `/u-vision` · `/api/u-vision` 아래에서 응답하고, 화면 빌드가 `/u-vision/` 에 맞춰져 있다. 앞부분을 떼거나 바꾸지 않는다 — 다른 경로에 실으려면 화면을 그 경로의 `VITE_BASE` 로 다시 빌드해야 한다(패키지 README «제약»).
- 업로드 상한은 엔진 쪽(`MAX_UPLOAD_SIZE_MB`, 기본 10)이 답하도록 프록시를 조금 더 크게(`client_max_body_size 12m`).
- 판정은 비전 모델만큼 걸리므로 API 쪽 읽기 시간을 넉넉히(`proxy_read_timeout 120s`).
- `absolute_redirect off` — `/u-vision` → `/u-vision/` 이동이 클라이언트가 쓴 포트 · 스킴을 지킨다.
- 관리 요청의 `X-Admin-Pin` 헤더는 그대로 통과한다(nginx 기본).

ASP.NET Core 업무 앱이라면 nginx 대신 앱 앞의 리버스 프록시(YARP 등)로 같은 두 경로를 같은 규칙으로 넘기면 된다.

## 업무 앱 서버에서 부르기 (.NET)

업무 앱의 서버 코드는 같은 프록시 주소로 엔진 API 를 부른다 — 계약에서 생성한 [`UVision.Client`](../../server/src/UVision.Client/README.md)(MIT):

```csharp
var client = UVisionClient.Create(httpClient, new Uri("http://localhost:8200/"));
var health = await client.Health.GetAsync();

var form = new MultipartBody();
form.AddOrReplacePart("image", "image/jpeg", File.OpenRead("capture.jpg"), "capture.jpg");
form.AddOrReplacePart("scenario_id", "text/plain", "pcb-top");
var result = await client.V2.Inspect.PostAsync(form, c => c.Headers.Add("Idempotency-Key", captureId));
```

관리 호출(시나리오 생성 등)은 `httpClient` 에 `X-Admin-Pin` 헤더를 붙인다.

## 운영에서 더할 것

- **HTTPS** — 태블릿 카메라(`getUserMedia`)는 secure context 가 필요하다. 프록시에서 TLS 를 끝낸다.
- **데이터** — 엔진의 모든 데이터는 `u-vision-data` 볼륨(`/data`)에 있다. 백업 대상이다.
- **관리 PIN** — `ADMIN_PIN` 을 주거나, 비워 두고 첫 기동 때 `/data/admin-setup-token.txt` 의 값으로 관리 화면에서 정한다.

> 확인된 것: 프록시 너머로 화면 · manifest · service worker · API(시나리오 생성 · 판정) 응답, `/u-vision` 이동, 엔진 포트 비공개(데스크톱 브라우저 없이 HTTP 로). `UVision.Client` 로 같은 프록시 너머 왕복 — health · 시나리오 생성 · 삭제 · v1 · v2 판정 · `Idempotency-Key` 재전송(같은 판정 id) · 결과 조회 · 없는 시나리오 404 · PIN 없는 관리 호출 401(mock VLM). 태블릿(iOS Safari · Android Chrome)에서의 카메라 · 설치 동작은 아직 실기기로 확인하지 않았다.
