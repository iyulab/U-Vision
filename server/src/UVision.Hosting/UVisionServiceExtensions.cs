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

        // 파일시스템 저장소.
        services.AddSingleton(new StoragePaths(options.Storage, contentRoot));
        services.AddSingleton<IScenarioStore, FileScenarioStore>();
        services.AddSingleton<IInspectionStore, FileInspectionStore>();
        services.AddSingleton<IReferenceStore, FileReferenceStore>();
        services.AddSingleton<ILabelStore, FileLabelStore>();

        // 관리자 PIN.
        services.AddSingleton(new AdminPinOptions { Pin = options.AdminPin });

        // ⚠️ 전역 ConfigureHttpJsonOptions 를 등록하지 않는다(프로세스 전역 오염 방지).
        // wire snake_case 는 Task 4B 의 [JsonPropertyName] 으로 DTO 자체가 보유 → ambient 정책 무관.
        // Verdict "OK"/"NG" 도 모델 부착 [JsonConverter] 로 자기서술.

        return services;
    }
}
