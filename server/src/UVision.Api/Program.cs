using UVision.Api;
using UVision.Api.Endpoints;

// 개발 편의: server/.env 로딩(존재 시). NoClobber — 이미 설정된 실제 환경변수가 .env 보다 우선.
DotNetEnv.Env.TraversePath().NoClobber().Load();

var builder = WebApplication.CreateBuilder(args);

// .env/환경변수 → UVision 섹션 매핑(standalone dev 의 평면 키 UX 보존).
builder.Configuration.AddInMemoryCollection(StandaloneEnv.ToConfig());

builder.Services.AddUVision(builder.Configuration, builder.Environment.ContentRootPath);

// 주의: 전역 ConfigureHttpJsonOptions 를 추가하지 않는다. wire snake_case 는 DTO [JsonPropertyName]
// 이 보유(Task 4B) — standalone/임베드 모두 동일하게 동작. 재추가 금지(임베드 시 호스트 오염원).

// 단일 조직 셀프호스트 — CORS 는 관대하게(배포 시 reverse proxy 에서 조인다).
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();
app.UseUVision();
app.MapUVisionEndpoints(
    app.Services.GetRequiredService<UVisionOptions>().ApiBasePath);

app.Run();

// WebApplicationFactory(통합 테스트)용 진입점 노출.
public partial class Program;
