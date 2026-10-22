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
        // 쓰기 가능한 임시 경로 — StoragePaths 생성자가 디렉터리를 만든다(루트 `/data` 는 Linux 에서 권한 없음).
        var root = Path.Combine(Path.GetTempPath(), "uvision-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(new StorageOptions { DataPath = root }, AppContext.BaseDirectory);
        try
        {
            Assert.EndsWith(Path.Combine("demo", "authority.json"), paths.AuthorityFile("demo"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
