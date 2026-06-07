using System.Text.Json;
using UVision.Api.Auth;
using UVision.Api.Configuration;
using UVision.Api.Endpoints;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;

// 개발 편의: server/.env 로딩(존재 시). NoClobber — 이미 설정된 실제 환경변수가 .env 보다 우선한다
// (배포 플랫폼이 주입한 env 가 우연히 남은 .env 에 덮이지 않게; 테스트가 provider 를 고정할 수 있게).
// (원본 Python: pydantic-settings 의 .env 로딩과 동등 UX)
DotNetEnv.Env.TraversePath().NoClobber().Load();

var builder = WebApplication.CreateBuilder(args);

// VLM/업로드 설정 — 평면 환경변수 바인딩(원본 .env 키 이름 보존).
var vlmOptions = new VlmOptions
{
    Provider = Environment.GetEnvironmentVariable("VLM_PROVIDER") ?? "mock",
    Model = Environment.GetEnvironmentVariable("VLM_MODEL") ?? "gpt-4o",
    ApiKey = Environment.GetEnvironmentVariable("VLM_API_KEY") ?? "",
    Endpoint = Environment.GetEnvironmentVariable("VLM_ENDPOINT") ?? "",
    MaxUploadSizeMb = int.TryParse(Environment.GetEnvironmentVariable("MAX_UPLOAD_SIZE_MB"), out var mb) ? mb : 10,
};
builder.Services.AddSingleton(vlmOptions);

// IVlmProvider 는 singleton — ironhive hive 를 1회만 빌드한다.
builder.Services.AddSingleton<IVlmProvider>(_ => VlmProviderFactory.Create(vlmOptions));

// 파일시스템 저장소 — Storage:DataPath(appsettings, 환경변수 override 가능) 아래에 영속화.
var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName)
    .Get<StorageOptions>() ?? new StorageOptions();
builder.Services.AddSingleton(new StoragePaths(storageOptions, builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IScenarioStore, FileScenarioStore>();
builder.Services.AddSingleton<IInspectionStore, FileInspectionStore>();
builder.Services.AddSingleton<IReferenceStore, FileReferenceStore>();

// 관리자 PIN — 미설정 시 관리 엔드포인트는 503(운영은 무인증으로 정상 동작).
builder.Services.AddSingleton(new AdminPinOptions
{
    Pin = Environment.GetEnvironmentVariable("ADMIN_PIN"),
});

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
app.MapScenarioEndpoints();
app.MapReferenceEndpoints();

app.Run();

// WebApplicationFactory(통합 테스트)용 진입점 노출.
public partial class Program;
