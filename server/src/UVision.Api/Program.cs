using System.Text;
using UVision.Api;
using UVision.Api.Endpoints;

// 이 프로세스의 출력은 파일 리다이렉트 · 서비스 관리자 · 로그 수집기가 읽는다. 콘솔 코드 페이지(영어 Windows 의 437 등)로 두면
// 한글 진단 문구(설정 검증 예외 · 재시도 로그)가 전부 '?' 로 쓰여 사유를 읽을 수 없으므로 UTF-8 로 고정한다.
// 콘솔 핸들이 없거나 바꿀 수 없는 환경에서는 기본 인코딩을 그대로 둔다.
try { Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
catch (IOException) { }

// 개발 편의: server/.env 로딩(존재 시). NoClobber — 이미 설정된 실제 환경변수가 .env 보다 우선.
DotNetEnv.Env.TraversePath().NoClobber().Load();

var builder = WebApplication.CreateBuilder(args);

// .env/환경변수 → UVision 섹션 매핑(standalone dev 의 평면 키 UX 보존).
builder.Configuration.AddInMemoryCollection(StandaloneEnv.ToConfig());

// 설정 검증은 서비스 등록에서 기동을 거부한다(모르는 provider · 잘못된 경로 · 겹치는 목적지 이름 …). 처리하지 않은 예외로 두면 .NET 이
// 프로세스를 abort 시켜 컨테이너의 종료 코드가 139 로 보이고 사유는 스택 트레이스 속에 묻힌다 — 사유 한 줄과 구별되는 종료 코드 2 로 끝낸다.
try
{
    builder.Services.AddUVision(builder.Configuration, builder.Environment.ContentRootPath);

    // OPC UA 서버(P4 사건 전송) — 기본 꺼짐. 서버 스택은 엔진 호스트에만 있다.
    builder.Services.AddUVisionOpcUa(builder.Configuration, builder.Environment.ContentRootPath);
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"설정이 잘못되어 시작하지 않습니다: {ex.Message}");
    return 2;
}

// 주의: 전역 ConfigureHttpJsonOptions 를 추가하지 않는다. wire snake_case 는 DTO [JsonPropertyName]
// 이 보유(Task 4B) — standalone/임베드 모두 동일하게 동작. 재추가 금지(임베드 시 호스트 오염원).

// 단일 조직 셀프호스트 — CORS 는 관대하게(배포 시 reverse proxy 에서 조인다).
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// API 계약 문서 — `/openapi/v1.json`. 정본 사본은 `server/openapi/u-vision.json`(OpenApiContractTests 가 대조).
builder.Services.AddOpenApi(o => o.AddUVisionContract());

var app = builder.Build();

app.MapOpenApi();
app.UseCors();
app.UseUVision();
app.MapUVisionEndpoints(
    app.Services.GetRequiredService<UVisionOptions>().ApiBasePath);

app.Run();
return 0;

// WebApplicationFactory(통합 테스트)용 진입점 노출.
public partial class Program;
