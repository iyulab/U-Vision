using UVision.Api;
using UVision.Api.Services.Oracle;
using Xunit;

namespace UVision.Tests;

public class OracleDiTests
{
    [Fact]
    public void DefaultOptions_ProviderNone_OracleDisabled()
    {
        var p = OracleProviderFactory.Create(new UVisionOptions().Oracle);
        Assert.False(p.IsEnabled);   // 기본 none → 스윕 BackgroundService 즉시 return
    }
}
