# MinimalHost — UVision.Hosting 최소 소비앱 예제

`UVision.Hosting` NuGet 패키지만 참조해 U-Vision 검사 PWA + API 를 **자기 서버 앱으로** 띄우는 최소 ASP.NET Core 앱. **패키지 추가 + 3줄 와이어링**으로 동작함을 보이는 living example.

> 이 앱은 엔진 서버 그 자체다 — 다른 업무 앱은 이 서버를 HTTP API · 웹훅으로 부른다. 업무 앱 프로세스 안에 패키지를 넣는 방식은 이행기 호환 경로다(패키지 README «어떻게 연동하나»).

```bash
cd samples/MinimalHost
dotnet run --urls http://localhost:8100
# → http://localhost:8100/u-vision/        (PWA)
# → http://localhost:8100/api/u-vision/health   (API, provider=mock)
```

핵심은 `Program.cs` 3줄(`AddUVision` / `UseUVision` / `MapUVisionEndpoints`)과 `appsettings.json` 의 `UVision` 섹션뿐이다. 통합 가이드·설정 레퍼런스는 패키지 README 참고:
[server/src/UVision.Hosting/README.md](../../server/src/UVision.Hosting/README.md).

> 이 예제는 `mock` provider 로 키 없이 동작한다. 실제 VLM 연동은 `UVision:Vlm` 설정 참고.
> 카메라(getUserMedia)는 secure context 필수 — 실디바이스 테스트는 HTTPS 또는 localhost.
