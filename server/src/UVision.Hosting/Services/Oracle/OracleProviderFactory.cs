using UVision.Api.Configuration;
using UVision.Api.Services.Vlm;

namespace UVision.Api.Services.Oracle;

/// <summary>OracleOptions.Provider 로 구체 오라클 선택(VlmProviderFactory 동형). cloud 는 E4 결선 후.</summary>
public static class OracleProviderFactory
{
    public static IOracleProvider Create(OracleOptions o)
    {
        switch (o.Provider.ToLowerInvariant())
        {
            case "none":
                return new DisabledOracleProvider();
            case "gpustack":
                return new VlmOracleProvider(
                    new IronHiveVlmProvider(IronHiveBuilders.GpuStack(o.Endpoint, o.ApiKey), "oracle-gpustack", o.Model),
                    isCloud: false);
            case "openai":
            case "google":
                throw new NotImplementedException("cloud 오라클은 E4 egress 결선 후 — 키 BLOCKED(vllm 패턴 동형)");
            default:
                throw new ArgumentException($"알 수 없는 oracle provider: {o.Provider}");
        }
    }
}
