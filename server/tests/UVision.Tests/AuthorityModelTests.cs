using System.Text.Json;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Authority;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

public class AuthorityModelTests
{
    [Theory]
    [InlineData(AuthorityStage.Shadow, "shadow")]
    [InlineData(AuthorityStage.Advisory, "advisory")]
    [InlineData(AuthorityStage.CoPrimary, "co_primary")]
    [InlineData(AuthorityStage.MlPrimary, "ml_primary")]
    public void Stage_SerializesSnakeCase(AuthorityStage stage, string wire)
    {
        var json = JsonSerializer.Serialize(stage, StoragePaths.Json);
        Assert.Equal($"\"{wire}\"", json);
        Assert.Equal(stage, JsonSerializer.Deserialize<AuthorityStage>(json, StoragePaths.Json));
    }

    [Fact]
    public void Stage_UnknownDeserializesToAdvisory() =>
        Assert.Equal(AuthorityStage.Advisory,
            JsonSerializer.Deserialize<AuthorityStage>("\"bogus\"", StoragePaths.Json));

    [Fact]
    public void Resolve_NullState_IsAdvisory() =>
        Assert.Equal(AuthorityStage.Advisory, AuthorityResolver.Resolve(null));

    [Fact]
    public void Resolve_UsesStateStage() =>
        Assert.Equal(AuthorityStage.CoPrimary, AuthorityResolver.Resolve(
            new AuthorityState { Stage = AuthorityStage.CoPrimary, UpdatedAt = "t", UpdatedBy = "x" }));

    [Fact]
    public void AuthorityFile_IsScenarioRootJson()
    {
        var paths = new StoragePaths(new StorageOptions { DataPath = "/data" }, AppContext.BaseDirectory);
        Assert.EndsWith(Path.Combine("demo", "authority.json"), paths.AuthorityFile("demo"));
    }
}
