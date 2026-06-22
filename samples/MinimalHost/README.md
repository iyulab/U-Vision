# MinimalHost — UVision.Hosting 최소 소비앱 예제

`UVision.Hosting` NuGet 패키지만 참조해 U-Vision 검사 PWA + API 를 호스팅하는 최소 ASP.NET Core 앱. **패키지 추가 + 3줄 와이어링**으로 동작함을 보이는 living example.

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
