using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

public sealed class AuthorityStoreTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), "uv-auth-" + Guid.NewGuid().ToString("N"));

    private FileAuthorityStore NewStore() =>
        new(new StoragePaths(new StorageOptions { DataPath = _dataPath }, AppContext.BaseDirectory));

    [Fact]
    public async Task Read_AbsentFile_ReturnsNull() =>
        Assert.Null(await NewStore().ReadAsync("demo", default));

    [Fact]
    public async Task SetStage_WritesStateAndHistory()
    {
        var store = NewStore();
        await store.SetStageAsync("demo", AuthorityStage.CoPrimary, "device-x", "promote", "ml strong", default);

        var state = await store.ReadAsync("demo", default);
        Assert.NotNull(state);
        Assert.Equal(AuthorityStage.CoPrimary, state!.Stage);
        Assert.Equal(AuthorityStage.Advisory, state.PreviousStage); // 최초 baseline = advisory
        Assert.Single(state.History);
        Assert.Equal("advisory", state.History[0].From);
        Assert.Equal("co_primary", state.History[0].To);
        Assert.Equal("promote", state.History[0].Mode);
    }

    [Fact]
    public async Task SetStage_RecordsPreviousAndAppendsHistory()
    {
        var store = NewStore();
        await store.SetStageAsync("demo", AuthorityStage.CoPrimary, "x", "promote", null, default);
        await store.SetStageAsync("demo", AuthorityStage.MlPrimary, "x", "promote", null, default);

        var state = await store.ReadAsync("demo", default);
        Assert.Equal(AuthorityStage.MlPrimary, state!.Stage);
        Assert.Equal(AuthorityStage.CoPrimary, state.PreviousStage);
        Assert.Equal(2, state.History.Count);
    }

    [Fact]
    public async Task SetStage_RejectsInvalidScenarioId() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewStore().SetStageAsync("../escape", AuthorityStage.Shadow, "x", "promote", null, default));

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }
}
