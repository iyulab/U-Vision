using IronHive.Abstractions;
using IronHive.Abstractions.Messages;
using IronHive.Core;
using IronHive.Providers.GoogleAI;
using IronHive.Providers.OpenAI;
using IronHive.Providers.OpenAI.Compatible.GpuStack;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Configuration;

namespace UVision.Api.Services.Vlm;

/// <summary>
/// 설정의 Provider 값으로 구체 provider 를 선택한다.
/// 앱 코드는 <see cref="IVlmProvider"/> 만 알고 구현체를 모른다.
/// (원본: server/app/services/vlm/__init__.py get_provider)
/// </summary>
public static class VlmProviderFactory
{
    public static IVlmProvider Create(VlmOptions options)
    {
        switch (options.Provider.ToLowerInvariant())
        {
            case "mock":
                return new MockVlmProvider();

            case "openai":
                return new IronHiveVlmProvider(
                    IronHiveBuilders.BuildMessageService(b => b.AddOpenAIProviders(
                        "openai",
                        new OpenAIConfig { ApiKey = options.ApiKey },
                        OpenAIServiceType.ChatCompletion)),
                    "openai",
                    options.Model);

            case "google":
                return new IronHiveVlmProvider(
                    IronHiveBuilders.BuildMessageService(b => b.AddGoogleAIProviders(
                        "google",
                        new GoogleAIConfig { ApiKey = options.ApiKey })),
                    "google",
                    options.Model);

            case "gpustack":
                // GPUStack — OpenAI 호환 셀프호스트(/v1-openai/). ironhive 1급 provider 가 base URL·경로를 흡수.
                // Endpoint 는 경로 없는 서버 base URL(예: http://host:8080) — GpuStackConfig 가 /v1-openai/ 를 붙인다.
                return new IronHiveVlmProvider(
                    IronHiveBuilders.GpuStack(options.Endpoint, options.ApiKey),
                    "gpustack",
                    options.Model);

            case "vllm":
                // P5(엣지 추론)에서 구현. 인터페이스만 예약.
                throw new NotImplementedException("vLLM 어댑터는 P5(엣지 추론 모드)에서 구현 예정");

            default:
                throw new ArgumentException($"알 수 없는 VLM provider: {options.Provider}");
        }
    }

}
