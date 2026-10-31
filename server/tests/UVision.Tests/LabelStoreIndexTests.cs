using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>라벨 저장소의 날짜별 메모리 색인 — 목록이 사이드카를 다시 열지 않아도 이 저장소의 쓰기를 따라가고, 폴더가 사라지면 비워진다.</summary>
public class LabelStoreIndexTests : IDisposable
{
    private const string Scenario = "idx";
    private const string Date = "2026-10-31";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvtest_" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public LabelStoreIndexTests() =>
        _paths = new StoragePaths(new StorageOptions { DataPath = _root }, _root);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task List_FollowsEveryKindOfWrite_WithoutReopeningTheSidecars()
    {
        var store = new FileLabelStore(_paths);
        await store.AppendLabelAsync(Scenario, Date, "a", "OK", "dev");
        Assert.Single(await store.ListAsync(Scenario, Date)); // 처음 적재

        await store.AppendLabelAsync(Scenario, Date, "b", "NG", "dev");
        await store.AppendAbstainAsync(Scenario, Date, "c", "dev");
        await store.AppendOracleAsync(Scenario, Date, "a", "NG", "oracle");
        await store.AppendAuditAsync(Scenario, Date, "b", "OK", "auditor");
        // 파일을 지워도 색인이 답한다 — 목록은 이 저장소의 쓰기를 따라가는 파생물이다.
        foreach (var f in Directory.GetFiles(_paths.DateDir(Scenario, Date), "*.label.json"))
            File.Delete(f);

        var listed = (await store.ListAsync(Scenario, Date)).OrderBy(l => l.ImageId).ToList();
        Assert.Equal(["a", "b", "c"], listed.Select(l => l.ImageId));
        Assert.Equal("OK", listed[0].OperativeLabel);           // 오라클은 운영 라벨을 바꾸지 않는다
        Assert.Contains(listed[0].History!, e => e.Mode == LabelMode.Oracle);
        Assert.Equal(LabelAuditStatus.Conflicted, listed[1].Audit?.Status); // 감사가 운영 라벨과 달라 충돌
        Assert.True(listed[2].AbstainCount > 0 && listed[2].OperativeLabel is null);
    }

    [Fact]
    public async Task List_DropsADeletedLabel()
    {
        var store = new FileLabelStore(_paths);
        await store.AppendLabelAsync(Scenario, Date, "a", "OK", "dev");
        await store.AppendLabelAsync(Scenario, Date, "b", "OK", "dev");
        Assert.Equal(2, (await store.ListAsync(Scenario, Date)).Count);

        await store.DeleteAsync(Scenario, Date, "a");

        Assert.Equal(["b"], (await store.ListAsync(Scenario, Date)).Select(l => l.ImageId));
    }

    [Fact]
    public async Task List_LoadsExistingSidecarsOnFirstCall_AndDropsTheIndexWhenTheDayFolderIsGone()
    {
        await new FileLabelStore(_paths).AppendLabelAsync(Scenario, Date, "a", "OK", "dev"); // 이전 프로세스가 쓴 파일
        var store = new FileLabelStore(_paths);                                              // 새 프로세스
        Assert.Single(await store.ListAsync(Scenario, Date));

        Directory.Delete(_paths.DateDir(Scenario, Date), true);
        Assert.Empty(await store.ListAsync(Scenario, Date));

        await store.AppendLabelAsync(Scenario, Date, "z", "NG", "dev");                     // 같은 날짜로 다시 써도 낡은 라벨이 되살아나지 않는다
        Assert.Equal(["z"], (await store.ListAsync(Scenario, Date)).Select(l => l.ImageId));
    }

    [Fact]
    public async Task List_SeesEveryConcurrentWrite()
    {
        var store = new FileLabelStore(_paths);
        await store.AppendLabelAsync(Scenario, Date, "seed", "OK", "dev");
        await store.ListAsync(Scenario, Date); // 적재해 두고 쓰기가 색인을 고치게 한다

        await Task.WhenAll(Enumerable.Range(0, 60).Select(i =>
            store.AppendLabelAsync(Scenario, Date, $"img{i:D3}", i % 2 == 0 ? "OK" : "NG", "dev")));

        Assert.Equal(61, (await store.ListAsync(Scenario, Date)).Count);
    }
}
