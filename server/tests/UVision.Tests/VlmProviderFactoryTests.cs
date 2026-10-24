using UVision.Api.Configuration;
using UVision.Api.Services.Vlm;
using Xunit;

namespace UVision.Tests;

/// <summary>VLM provider 설정 값 — 지어진 값만 받고, 그 밖은 받는 값을 알려 주며 거부한다.</summary>
public class VlmProviderFactoryTests
{
    [Fact]
    public void OpenAICompatible_BuildsFromTheServerAddress()
    {
        var p = VlmProviderFactory.Create(new VlmOptions
        {
            Provider = "openai-compatible", Endpoint = "http://localhost:9999", Model = "qwen2.5-vl",
        });
        Assert.Equal("openai-compatible", p.Name);
    }

    [Fact]
    public void OpenAICompatible_WithoutEndpoint_IsRejected() =>
        Assert.Throws<ArgumentException>(() =>
            VlmProviderFactory.Create(new VlmOptions { Provider = "openai-compatible" }));

    [Theory]
    [InlineData("gpustack")] // 옛 이름 — openai-compatible 로 옮긴다
    [InlineData("vllm")]     // 예약만 있던 값 — vLLM 은 openai-compatible 로 붙는다
    [InlineData("nope")]
    public void UnbuiltProvider_IsRejected_NamingTheAcceptedValues(string provider)
    {
        var ex = Assert.Throws<ArgumentException>(() => VlmProviderFactory.Create(new VlmOptions { Provider = provider }));
        Assert.Contains("openai-compatible", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(601)]
    public void TimeoutOutOfRange_IsRejected(int seconds) =>
        Assert.Throws<ArgumentException>(() =>
            VlmProviderFactory.Create(new VlmOptions { Provider = "mock", TimeoutSeconds = seconds }));
}
