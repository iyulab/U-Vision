using UVision.Api.Configuration;
using UVision.Api.Services.Vlm;

namespace UVision.Api.Services.Oracle;

/// <summary>OracleOptions.Provider 로 구체 오라클 선택(VlmProviderFactory 동형). 셀프호스트만 — cloud 오라클은 없다.</summary>
public static class OracleProviderFactory
{
    public static IOracleProvider Create(OracleOptions o)
    {
        switch (o.Provider.ToLowerInvariant())
        {
            case "none":
                return new DisabledOracleProvider();
            case ProviderNames.OpenAICompatible:
                if (string.IsNullOrWhiteSpace(o.Endpoint))
                    throw new ArgumentException("oracle provider openai-compatible 은 Endpoint(서버 주소)가 필요하다");
                return new VlmOracleProvider(
                    new IronHiveVlmProvider(IronHiveBuilders.OpenAICompatible(o.Endpoint, o.ApiKey), "oracle", o.Model),
                    isCloud: false);
            default:
                throw new ArgumentException(ProviderNames.Unknown("oracle", o.Provider, ProviderNames.Oracle));
        }
    }
}
