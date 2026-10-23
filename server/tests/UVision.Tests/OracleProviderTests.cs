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
    public void OpenAICompatible_IsEnabled_NotCloud()
    {
        var p = OracleProviderFactory.Create(new OracleOptions
        {
            Provider = "openai-compatible", Endpoint = "http://localhost:9999", Model = "big",
        });
        Assert.True(p.IsEnabled);
        Assert.False(p.IsCloud);
    }

    [Theory]
    [InlineData("openai")]   // cloud 오라클은 없다 — 이미지가 밖으로 나가는 경로
    [InlineData("google")]
    [InlineData("gpustack")] // 옛 이름 — openai-compatible 로 옮긴다
    public void UnbuiltProvider_IsRejected_NamingTheAcceptedValues(string provider)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OracleProviderFactory.Create(new OracleOptions { Provider = provider }));
        Assert.Contains("openai-compatible", ex.Message);
    }

    [Fact]
    public void OpenAICompatible_WithoutEndpoint_IsRejected() =>
        Assert.Throws<ArgumentException>(() =>
            OracleProviderFactory.Create(new OracleOptions { Provider = "openai-compatible" }));
}
