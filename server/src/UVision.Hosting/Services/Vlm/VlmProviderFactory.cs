using IronHive.Abstractions;
using IronHive.Abstractions.Messages;
using IronHive.Core;
using IronHive.Providers.GoogleAI;
using IronHive.Providers.OpenAI;
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
        if (options.TimeoutSeconds is < 0 or > 600)
            throw new ArgumentException("UVision:Vlm:TimeoutSeconds 는 0(한도 없음) 또는 1~600 이어야 합니다");
        if (options.MaxOutputTokens is < 256 or > 131072)
            throw new ArgumentException("UVision:Vlm:MaxOutputTokens 는 256~131072 이어야 합니다");

        switch (options.Provider.ToLowerInvariant())
        {
            case "mock":
                return new MockVlmProvider();

            case "openai":
                // UNVERIFIED: 실호출 미검증(API 키 없음) — 구조화 출력 응답 형태 · 지연. IronHiveVlmProvider 머리 주석.
                return new IronHiveVlmProvider(
                    IronHiveBuilders.BuildMessageService(b => b.AddOpenAIProviders(
                        "openai",
                        new OpenAIConfig { ApiKey = options.ApiKey },
                        OpenAIServiceType.Messages)),
                    "openai",
                    options.Model,
                    options.MaxOutputTokens);

            case "google":
                // UNVERIFIED: 실호출 미검증(API 키 없음) — 구조화 출력 응답 형태 · 지연. IronHiveVlmProvider 머리 주석.
                return new IronHiveVlmProvider(
                    IronHiveBuilders.BuildMessageService(b => b.AddGoogleAIProviders(
                        "google",
                        new GoogleAIConfig { ApiKey = options.ApiKey })),
                    "google",
                    options.Model,
                    options.MaxOutputTokens);

            case ProviderNames.OpenAICompatible:
                // 셀프호스트 서버 — Endpoint 는 서버 주소(예: http://host:8080), ironhive 가 /v1 을 붙인다.
                if (string.IsNullOrWhiteSpace(options.Endpoint))
                    throw new ArgumentException("VLM provider openai-compatible 은 Endpoint(서버 주소)가 필요하다");
                return new IronHiveVlmProvider(
                    IronHiveBuilders.OpenAICompatible(options.Endpoint, options.ApiKey),
                    ProviderNames.OpenAICompatible,
                    options.Model,
                    options.MaxOutputTokens);

            default:
                throw new ArgumentException(ProviderNames.Unknown("VLM", options.Provider, ProviderNames.Vlm));
        }
    }

}
