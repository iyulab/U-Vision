using UVision.Api.Configuration;
using UVision.Api.Services.Oracle;
using Xunit;

namespace UVision.Tests;

public class OracleProviderTests
{
    [Fact]
    public void None_IsDisabled()
    {
        var p = OracleProviderFactory.Create(new OracleOptions());
        Assert.False(p.IsEnabled);
        Assert.False(p.IsCloud);
    }

    [Fact]
    public void Gpustack_IsEnabled_NotCloud()
    {
        var p = OracleProviderFactory.Create(new OracleOptions
        {
            Provider = "gpustack", Endpoint = "http://localhost:9999", Model = "big",
        });
        Assert.True(p.IsEnabled);
        Assert.False(p.IsCloud);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("google")]
    public void Cloud_NotImplemented(string provider)
    {
        Assert.Throws<NotImplementedException>(() =>
            OracleProviderFactory.Create(new OracleOptions { Provider = provider }));
    }
}
