using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api;
using UVision.Api.Configuration;
using UVision.Api.Services.Ml;
using Xunit;

namespace UVision.Tests;

/// <summary>FW-2 — IMlClassifier seam(none/mock 팩토리·결정론·DI 배선). 현재 동작 무변경(none 기본).</summary>
public class MlClassifierTests
{
    private static readonly byte[] Sample = [10, 20, 30, 40, 50];

    [Fact]
    public void Factory_None_ReturnsDisabled()
    {
        var c = MlClassifierFactory.Create(new MlOptions { Provider = "none" });
        Assert.Equal("none", c.Name);
        Assert.False(c.IsEnabled);
    }

    [Fact]
    public void Factory_Mock_ReturnsEnabled()
    {
        var c = MlClassifierFactory.Create(new MlOptions { Provider = "mock" });
        Assert.Equal("mock", c.Name);
        Assert.True(c.IsEnabled);
    }

    [Fact]
    public void Factory_Mloop_RequiresEndpoint()
    {
        Assert.Throws<ArgumentException>(
            () => MlClassifierFactory.Create(new MlOptions { Provider = "mloop" }));
    }

    [Fact]
    public void Factory_Mloop_WithEndpoint_ReturnsEnabledClient()
    {
        var c = MlClassifierFactory.Create(
            new MlOptions { Provider = "mloop", Endpoint = "http://localhost:5000" });
        Assert.Equal("mloop", c.Name);
        Assert.True(c.IsEnabled);
    }

    [Fact]
    public void Factory_Unknown_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => MlClassifierFactory.Create(new MlOptions { Provider = "bogus" }));
    }

    [Fact]
    public async Task Disabled_Classify_Throws()
    {
        var c = new DisabledMlClassifier();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.ClassifyAsync(Sample, "demo"));
    }

    [Fact]
    public async Task Mock_IsDeterministic_AndWellFormed()
    {
        var c = new MockMlClassifier();
        var a = await c.ClassifyAsync(Sample, "demo");
        var b = await c.ClassifyAsync(Sample, "demo");

        Assert.Equal(a.Label, b.Label);            // 동일 바이트 → 동일 결과
        Assert.Equal(a.Confidence, b.Confidence);
        Assert.Contains(a.Label, new[] { "ok", "ng" });
        Assert.InRange(a.Confidence, 0.0, 1.0);
        Assert.Equal(1.0, a.Scores["ok"] + a.Scores["ng"], precision: 3); // 확률 합 ≈ 1
    }

    [Fact]
    public void AddUVision_Default_RegistersDisabledClassifier()
    {
        using var sp = BuildProvider(provider: null);
        var c = sp.GetRequiredService<IMlClassifier>();
        Assert.False(c.IsEnabled); // 기본 none — 현재 동작(VLM 단독) 무변경
    }

    [Fact]
    public void AddUVision_MockProvider_RegistersEnabledClassifier()
    {
        using var sp = BuildProvider(provider: "mock");
        Assert.True(sp.GetRequiredService<IMlClassifier>().IsEnabled);
    }

    private static ServiceProvider BuildProvider(string? provider)
    {
        var settings = new Dictionary<string, string?>
        {
            ["UVision:Vlm:Provider"] = "mock",
            ["UVision:Storage:DataPath"] = "data",
        };
        if (provider is not null)
            settings["UVision:Ml:Provider"] = provider;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUVision(config, contentRoot: Path.GetTempPath());
        return services.BuildServiceProvider();
    }
}
