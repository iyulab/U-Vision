using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Metrics;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>«모르겠음» 은 라벨이 아니라 기권 이벤트다 — operative 라벨 · 감사 · 측정에 아무 영향이 없고 횟수만 센다.</summary>
public class LabelStoreAbstainTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvtest_" + Guid.NewGuid().ToString("N"));
    private FileLabelStore Store() => new(new StoragePaths(new StorageOptions { DataPath = _root }, _root));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task Abstain_OnUnlabeled_LeavesItUnlabeled_AndCountsTheAbstention()
    {
        var s = Store();
        await s.AppendAbstainAsync("sc", "2026-10-31", "img1", "device-1");
        await s.AppendAbstainAsync("sc", "2026-10-31", "img1", "device-2");

        var l = await s.ReadAsync("sc", "2026-10-31", "img1");

        Assert.NotNull(l);
        Assert.Null(l!.OperativeLabel);        // 기권은 라벨이 아니다 — 미라벨 그대로
        Assert.Null(l.Label);
        Assert.Equal(2, l.AbstainCount);
        Assert.All(l.History!, e => Assert.Equal(LabelMode.Abstain, e.Mode));
    }

    [Fact]
    public async Task Abstain_PreservesTheOperativeLabelAndTheAuditState()
    {
        var s = Store();
        await s.AppendLabelAsync("sc", "2026-10-31", "img1", "NG", "device-1");
        await s.AppendAbstainAsync("sc", "2026-10-31", "img1", "device-2");

        var l = await s.ReadAsync("sc", "2026-10-31", "img1");

        Assert.Equal("NG", l!.OperativeLabel);
        Assert.Equal(1, l.AbstainCount);
        Assert.Equal(LabelAuditStatus.Unaudited, l.Audit?.Status ?? LabelAuditStatus.Unaudited);
    }

    [Fact]
    public async Task Abstain_CarriesTheStreamMarker_AndRejectsUnknownOnes()
    {
        var s = Store();
        await s.AppendAbstainAsync("sc", "2026-10-31", "img1", "device-1", stream: LabelStream.Learn);

        var l = await s.ReadAsync("sc", "2026-10-31", "img1");
        Assert.Equal(LabelStream.Learn, l!.History!.Single().Stream);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            s.AppendAbstainAsync("sc", "2026-10-31", "img1", "device-1", stream: "weird"));
    }

    [Fact]
    public async Task AbstainOnlySidecar_IsNotCountedAsALabelByTheMetrics()
    {
        var s = Store();
        await s.AppendAbstainAsync("sc", "2026-10-31", "img1", "device-1");
        var labels = await s.ListAsync("sc", "2026-10-31");
        var rows = new[]
        {
            new MetricsRow
            {
                ImageId = "img1", Timestamp = "2026-10-31T12:00:00.0000000+00:00", Verdict = Verdict.NG,
                VlmConfidence = 0.9, MlDegraded = false,
            },
        };

        var summary = MetricsAggregator.Summarize("sc", "2026-10-31", rows, labels, new AuthorityOptions());

        Assert.Equal(0, summary.Labeled);
        Assert.Equal(0, summary.LabeledNg);
    }
}
