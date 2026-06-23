using IronHive.Abstractions;
using IronHive.Abstractions.Messages;
using IronHive.Core;
using IronHive.Providers.OpenAI.Compatible.GpuStack;
using Microsoft.Extensions.DependencyInjection;

namespace UVision.Api.Services.Vlm;

/// <summary>ironhive IMessageService 빌더 — VLM·오라클 provider 가 공유(중복 제거).</summary>
public static class IronHiveBuilders
{
    public static IMessageService BuildMessageService(Action<HiveServiceBuilder> register)
    {
        var builder = new HiveServiceBuilder();
        register(builder);
        return builder.Build().Services.GetRequiredService<IMessageService>();
    }

    /// <summary>GPUStack(OpenAI 호환 셀프호스트) IMessageService — VLM·로컬 오라클 공용.</summary>
    public static IMessageService GpuStack(string baseUrl, string apiKey) =>
        BuildMessageService(b => b.AddGpuStackProviders(
            "gpustack", new GpuStackConfig { BaseUrl = baseUrl, ApiKey = apiKey }, GpuStackServiceType.Language));
}
