using IronHive.Abstractions;
using IronHive.Abstractions.Messages;
using IronHive.Core;
using IronHive.Providers.OpenAI.Compatible;

namespace UVision.Api.Services.Vlm;

/// <summary>ironhive IMessageService 빌더 — VLM·오라클 provider 가 공유(중복 제거).</summary>
public static class IronHiveBuilders
{
    public static IMessageService BuildMessageService(Action<HiveServiceBuilder> register)
    {
        var builder = new HiveServiceBuilder();
        register(builder);
        return builder.Build().Messages;
    }

    /// <summary>
    /// OpenAI 호환 셀프호스트 서버(GPUStack · vLLM · Ollama · 조직 게이트웨이 등) IMessageService — VLM · 로컬 오라클 공용.
    /// <paramref name="baseUrl"/> 은 서버 주소다 — ironhive 가 표준 경로 <c>/v1</c> 를 붙인다(이미 붙어 있으면 그대로).
    /// 키는 선택이다(LAN 서버는 키 없이 받기도 한다).
    /// </summary>
    public static IMessageService OpenAICompatible(string baseUrl, string apiKey) =>
        BuildMessageService(b => b.AddOpenAICompatibleProviders(
            ProviderNames.OpenAICompatible,
            new OpenAICompatibleConfig { BaseUrl = baseUrl, ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey },
            OpenAICompatibleServiceType.Language));
}
