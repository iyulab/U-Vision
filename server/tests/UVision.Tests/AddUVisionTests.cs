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
    }
}
