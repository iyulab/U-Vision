using UVision.Api;
using UVision.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// U-Vision 검사 기능 등록 — 설정 섹션 "UVision"에서 바인딩, 스토리지는 contentRoot 아래로 절대화.
builder.Services.AddUVision(builder.Configuration, builder.Environment.ContentRootPath);

var app = builder.Build();

// U-Vision PWA(BasePath, 기본 /u-vision) + 정적 에셋 + config 주입.
app.UseUVision();
// U-Vision API(ApiBasePath, 기본 /api/u-vision) 엔드포인트 매핑.
app.MapUVisionEndpoints(app.Services.GetRequiredService<UVisionOptions>().ApiBasePath);

app.Run();
