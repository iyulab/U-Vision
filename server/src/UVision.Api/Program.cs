using System.Text.Json;
using UVision.Api.Configuration;
using UVision.Api.Endpoints;
using UVision.Api.Services.Vlm;

// 개발 편의: server/.env 로딩(존재 시). 환경변수가 이미 있으면 그것이 우선.
// (원본 Python: pydantic-settings 의 .env 로딩과 동등 UX)
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// VLM/업로드 설정 — 평면 환경변수 바인딩(원본 .env 키 이름 보존).
var vlmOptions = new VlmOptions
{
    Provider = Environment.GetEnvironmentVariable("VLM_PROVIDER") ?? "mock",
    Model = Environment.GetEnvironmentVariable("VLM_MODEL") ?? "gpt-4o",
    ApiKey = Environment.GetEnvironmentVariable("VLM_API_KEY") ?? "",
    MaxUploadSizeMb = int.TryParse(Environment.GetEnvironmentVariable("MAX_UPLOAD_SIZE_MB"), out var mb) ? mb : 10,
};
builder.Services.AddSingleton(vlmOptions);

// IVlmProvider 는 singleton — ironhive hive 를 1회만 빌드한다.
builder.Services.AddSingleton<IVlmProvider>(_ => VlmProviderFactory.Create(vlmOptions));

// wire 계약 보존: snake_case 속성명(image_id 등). Verdict enum 의 "OK"/"NG" 는 모델 부착 converter.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

// 단일 조직 셀프호스트 — CORS 는 관대하게 두고 배포 시 reverse proxy 에서 조인다.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();
app.MapInspectEndpoints();

app.Run();

// WebApplicationFactory(통합 테스트)용 진입점 노출.
public partial class Program;
