using Microsoft.Extensions.Configuration;
using UVision.Api;
using Xunit;

namespace UVision.Tests;

public class UVisionOptionsTests
{
    [Fact]
    public void Binds_From_UVision_Section_With_Defaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UVision:Vlm:Provider"] = "openai",
                ["UVision:Storage:DataPath"] = "mydata",
                ["UVision:AdminPin"] = "1234",
            })
            .Build();

        var opts = config.GetSection("UVision").Get<UVisionOptions>() ?? new UVisionOptions();

        Assert.Equal("openai", opts.Vlm.Provider);
        Assert.Equal("mydata", opts.Storage.DataPath);
        Assert.Equal("1234", opts.AdminPin);
        Assert.Equal("/u-vision", opts.BasePath);        // 기본값
        Assert.Equal("/api/u-vision", opts.ApiBasePath); // 기본값
        Assert.Equal("U-Vision", opts.Title);            // 기본값
    }
}
