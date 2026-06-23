using UVision.Api.Configuration;
using UVision.Api.Services.Label;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

public class LabelStoreOracleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvtest_" + Guid.NewGuid().ToString("N"));
    private FileLabelStore Store() => new(new StoragePaths(new StorageOptions { DataPath = _root }, _root));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task AppendOracle_OnUnlabeled_CreatesSidecar_OperativeNull()
    {
        var s = Store();
        await s.AppendOracleAsync("sc", "2026-06-23", "img1", "NG", "oracle");
        var l = await s.ReadAsync("sc", "2026-06-23", "img1");
        Assert.NotNull(l);
        Assert.Null(l!.OperativeLabel);                       // operative 미설정
        Assert.Contains(l.History!, e => e.Mode == LabelMode.Oracle && e.Label == "NG");
    }

    [Fact]
    public async Task AppendOracle_PreservesHumanOperative()
    {
        var s = Store();
        await s.AppendLabelAsync("sc", "2026-06-23", "img1", "OK", "device-1");
        await s.AppendOracleAsync("sc", "2026-06-23", "img1", "NG", "oracle");
        var l = await s.ReadAsync("sc", "2026-06-23", "img1");
        Assert.Equal("OK", l!.OperativeLabel);                // 사람 라벨 불변
        Assert.Contains(l.History!, e => e.Mode == LabelMode.Oracle);
    }
}
