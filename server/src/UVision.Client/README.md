# UVision.Client

U-Vision 판정 엔진 서버를 부르는 **타입 있는 HTTP 클라이언트**. 엔진의 OpenAPI 계약([`server/openapi/u-vision.json`](https://github.com/iyulab/U-Vision/blob/main/server/openapi/u-vision.json))에서 [Kiota](https://learn.microsoft.com/openapi/kiota/) 로 생성한다 — 계약에 있는 경로 · 필드 · 오류 응답이 그대로 메서드 · 모델 · 예외가 된다.

- 엔진은 **별도 앱**(컨테이너 이미지 · 단독 서버)으로 돌고, 업무 앱은 이 클라이언트로 HTTP 너머에서 부른다.
- **MIT** — 이 패키지만 MIT 다. 엔진(서버 · `UVision.Hosting`)은 AGPL-3.0-or-later + 상용 라이선스. 네트워크 너머의 엔진을 부르는 앱에는 AGPL 이 미치지 않는다.

## 설치

```bash
dotnet add package UVision.Client
```

## 사용

```csharp
using Microsoft.Kiota.Abstractions;
using UVision.Client;

var http = httpClientFactory.CreateClient("u-vision");
var client = UVisionClient.Create(http, new Uri("https://host/vision")); // 프록시 접두까지가 주소

// 판정 — 계약의 multipart 폼은 Kiota 의 MultipartBody 로 보낸다(파트 이름 = 계약의 필드명)
var form = new MultipartBody();
form.AddOrReplacePart("image", "image/jpeg", File.OpenRead("capture.jpg"), "capture.jpg");
form.AddOrReplacePart("scenario_id", "text/plain", "pcb-top");
var result = await client.V2.Inspect.PostAsync(form);
Console.WriteLine($"{result?.Class} by {result?.SettledBy?.Tier}");

// 조회
var scenarios = await client.Scenarios.GetAsync();
```

- **동작 예제** — [`samples/ClientIntegration`](https://github.com/iyulab/U-Vision/tree/main/samples/ClientIntegration): 시나리오 목록 · 판정 · 검토 대기 · 503 · 거부 처리.
- **기준 경로** — 엔진의 API 기준 경로(기본 `/api/u-vision`)는 `Create` 의 `apiBasePath` 로 넘긴다. 경로는 그 아래 상대 경로다(`client.Inspect` = `{주소}{기준 경로}/inspect`).
- **관리 호출** — 시나리오 생성 · 수정 · 업로드 같은 변경은 `X-Admin-Pin` 헤더가 필요하다. 넘기는 `HttpClient` 에 붙이거나 요청마다 `requestConfiguration` 의 `Headers` 로 붙인다.
- **오류** — 계약에 적힌 오류 응답은 예외로 온다: `ProblemDetails`(400 · 404 · 413 · 415 · 422 · 500), 판정 불가는 `DetectionUnavailableResponse`(503). `ResponseStatusCode` 로 상태를 읽는다.
- **재전송** — 판정 요청을 다시 보낼 수 있으면 `Idempotency-Key` 헤더를 붙인다(같은 키는 첫 200 응답을 다시 받는다).
