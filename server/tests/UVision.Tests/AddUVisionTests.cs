using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Auth;
using UVision.Api.Configuration;
using UVision.Api.Services.Vlm;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

public class AddUVisionTests
{
    [Fact]
    public void Registers_Core_Services_From_Config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UVision:Vlm:Provider"] = "mock",
                ["UVision:Storage:DataPath"] = "data",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUVision(config, contentRoot: Path.GetTempPath());

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IVlmProvider>());
        Assert.NotNull(sp.GetRequiredService<IScenarioStore>());
        Assert.NotNull(sp.GetRequiredService<IInspectionStore>());
        Assert.NotNull(sp.GetRequiredService<IReferenceStore>());
        Assert.NotNull(sp.GetRequiredService<ILabelStore>());
        Assert.NotNull(sp.GetRequiredService<UVisionOptions>());
        Assert.NotNull(sp.GetRequiredService<AdminPinOptions>());
        Assert.NotNull(sp.GetRequiredService<VlmOptions>());
        // 기억 단은 기본 꺼짐 — 등록은 되지만 판정에 끼지 않는다.
        Assert.False(sp.GetRequiredService<UVision.Api.Services.Memory.IImageEmbedder>().IsEnabled);
        Assert.NotNull(sp.GetRequiredService<IMemoryVectorStore>());
    }
}
