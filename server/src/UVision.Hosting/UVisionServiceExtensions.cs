using UVision.Api.Auth;
using UVision.Api.Configuration;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;

namespace UVision.Api;

public static class UVisionServiceExtensions
{
    /// <summary>
    /// U-Vision 코어 서비스를 DI에 등록한다. <paramref name="contentRoot"/> 아래로 스토리지를 절대화한다.
    /// </summary>
    public static IServiceCollection AddUVision(
        this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var options = configuration.GetSection(UVisionOptions.SectionName).Get<UVisionOptions>()
            ?? new UVisionOptions();
        services.AddSingleton(options);

        // VLM provider — singleton(ironhive hive 1회 빌드).
        services.AddSingleton(options.Vlm);
        services.AddSingleton<IVlmProvider>(_ => VlmProviderFactory.Create(options.Vlm));

        // 전용 ML 분류기(신뢰성 플라이휠 ②~③) — 기본 none(VLM 단독, 현재 동작 무변경).
        services.AddSingleton(options.Ml);
        services.AddSingleton<Services.Ml.IMlClassifier>(sp =>
            Services.Ml.MlClassifierFactory.Create(
                options.Ml, sp.GetService<Services.Models.ModelBindingResolver>()));

        // A3: confidence 표준화 — 콜드스타트 정적 변환(데이터 쌓이면 캘리브레이션 맵으로 교체).
        services.AddSingleton<Services.Confidence.IConfidenceCalibrator,
            Services.Confidence.StaticConfidenceCalibrator>();

        // 파일시스템 저장소.
        services.AddSingleton(new StoragePaths(options.Storage, contentRoot));
        services.AddSingleton<IScenarioStore, FileScenarioStore>();
        services.AddSingleton<IInspectionStore, FileInspectionStore>();
        services.AddSingleton<IReferenceStore, FileReferenceStore>();
        services.AddSingleton<ILabelStore, FileLabelStore>();

        // 모델 버저닝(신뢰성 플라이휠 B1) — 시나리오별 모델 참조·이력 레지스트리.
        services.AddSingleton<IModelRegistry, FileModelRegistry>();
        services.AddSingleton<Services.Models.ModelBindingResolver>();

        // 관측성 메트릭(신뢰성 플라이휠 B3) — inspect 예측 신호의 append-only 시계열.
        services.AddSingleton<IMetricsStore, FileMetricsStore>();

        // 데이터셋 export(신뢰성 플라이휠 ② — 전용 ML 빌드 데이터 준비).
        services.AddSingleton<Services.Dataset.IDatasetExporter, Services.Dataset.FileDatasetExporter>();

        // 라벨 감사(C1) 옵션.
        services.Configure<LabelAuditOptions>(configuration.GetSection("UVision:LabelAudit"));

        // 관리자 PIN.
        services.AddSingleton(new AdminPinOptions { Pin = options.AdminPin });

        // ⚠️ 전역 ConfigureHttpJsonOptions 를 등록하지 않는다(프로세스 전역 오염 방지).
        // wire snake_case 는 Task 4B 의 [JsonPropertyName] 으로 DTO 자체가 보유 → ambient 정책 무관.
        // Verdict "OK"/"NG" 도 모델 부착 [JsonConverter] 로 자기서술.

        return services;
    }
}
