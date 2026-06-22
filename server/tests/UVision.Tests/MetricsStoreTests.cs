using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 메트릭 저장소 단위 테스트(B3) — append-only jsonl 왕복·날짜 버킷·동시성 직렬화·손상 내성.
/// 집계(rate·NG recall)는 C2, 여기선 write/read 메커니즘만 검증한다.
/// </summary>
public class MetricsStoreTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), "uvision-metrics-" + Guid.NewGuid().ToString("N"));

    private IMetricsStore Store =>
        new FileMetricsStore(new StoragePaths(
            new StorageOptions { DataPath = _dataPath }, AppContext.BaseDirectory));

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    private static MetricsRow Row(string imageId, string date, bool degraded = false) => new()
    {
        ImageId = imageId,
        Timestamp = $"{date}T12:00:00.0000000+00:00",
        Verdict = Verdict.NG,
        VlmConfidence = 0.9,
        MlLabel = degraded ? null : "ng",
        MlConfidence = degraded ? null : 0.95,
        Agreement = degraded ? null : true,
        RequiresReview = degraded ? null : false,
        MlDegraded = degraded,
    };

    [Fact]
    public async Task Append_Then_Read_RoundTrips()
    {
        var store = Store;
        await store.AppendAsync("demo", Row("img_a", "2026-06-22"));

        var rows = await store.ReadAsync("demo", "2026-06-22");

        var row = Assert.Single(rows);
        Assert.Equal("img_a", row.ImageId);
        Assert.Equal(Verdict.NG, row.Verdict);
        Assert.Equal(0.9, row.VlmConfidence);
        Assert.Equal("ng", row.MlLabel);
        Assert.Equal(0.95, row.MlConfidence);
        Assert.True(row.Agreement);
        Assert.False(row.RequiresReview);
        Assert.False(row.MlDegraded);
    }

    [Fact]
    public async Task Append_Accumulates_InOrder_SameDate()
    {
        var store = Store;
        await store.AppendAsync("demo", Row("img_a", "2026-06-22"));
        await store.AppendAsync("demo", Row("img_b", "2026-06-22"));
        await store.AppendAsync("demo", Row("img_c", "2026-06-22"));

        var rows = await store.ReadAsync("demo", "2026-06-22");

        Assert.Equal(["img_a", "img_b", "img_c"], rows.Select(r => r.ImageId));
    }

    [Fact]
    public async Task Append_SeparatesByDate()
    {
        var store = Store;
        await store.AppendAsync("demo", Row("img_a", "2026-06-22"));
        await store.AppendAsync("demo", Row("img_b", "2026-06-23"));

        Assert.Equal("img_a", Assert.Single(await store.ReadAsync("demo", "2026-06-22")).ImageId);
        Assert.Equal("img_b", Assert.Single(await store.ReadAsync("demo", "2026-06-23")).ImageId);
    }

    [Fact]
    public async Task Append_DerivesDateFromTimestampUtc()
    {
        var store = Store;
        // KST 02:00(2026-06-23) = UTC 17:00(2026-06-22) → UTC 날짜 버킷 2026-06-22 에 들어가야 한다.
        await store.AppendAsync("demo", new MetricsRow
        {
            ImageId = "img_tz",
            Timestamp = "2026-06-23T02:00:00.0000000+09:00",
            Verdict = Verdict.OK,
            VlmConfidence = 0.5,
            MlDegraded = false,
        });

        Assert.Single(await store.ReadAsync("demo", "2026-06-22"));
        Assert.Empty(await store.ReadAsync("demo", "2026-06-23"));
    }

    [Fact]
    public async Task Append_Degraded_PersistsNullMlFields()
    {
        var store = Store;
        await store.AppendAsync("demo", Row("img_d", "2026-06-22", degraded: true));

        var row = Assert.Single(await store.ReadAsync("demo", "2026-06-22"));
        Assert.True(row.MlDegraded);
        Assert.Null(row.MlLabel);
        Assert.Null(row.MlConfidence);
        Assert.Null(row.Agreement);
        Assert.Null(row.RequiresReview);
    }

    [Fact]
    public async Task ConcurrentAppends_AllPreserved()
    {
        var store = Store;
        var tasks = Enumerable.Range(0, 50)
            .Select(i => store.AppendAsync("demo", Row($"img_{i:D2}", "2026-06-22")));
        await Task.WhenAll(tasks);

        var rows = await store.ReadAsync("demo", "2026-06-22");
        Assert.Equal(50, rows.Count);
        // 직렬화 증명 — 인터리브로 줄이 섞이면 파싱 실패→누락되어 count 가 줄어든다.
        Assert.Equal(
            Enumerable.Range(0, 50).Select(i => $"img_{i:D2}").OrderBy(x => x),
            rows.Select(r => r.ImageId).OrderBy(x => x));
    }

    [Fact]
    public async Task Read_SkipsCorruptLines()
    {
        var store = Store;
        await store.AppendAsync("demo", Row("img_a", "2026-06-22"));

        // 손상된 줄(append 도중 크래시 시뮬)을 끼워 넣는다 — read 가 멈추지 않고 skip 해야 한다.
        var path = new StoragePaths(new StorageOptions { DataPath = _dataPath }, AppContext.BaseDirectory)
            .MetricsJsonl("demo", "2026-06-22");
        await File.AppendAllTextAsync(path, "{ broken json\n");
        await store.AppendAsync("demo", Row("img_b", "2026-06-22"));

        var rows = await store.ReadAsync("demo", "2026-06-22");
        Assert.Equal(["img_a", "img_b"], rows.Select(r => r.ImageId));
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(await Store.ReadAsync("demo", "2026-06-22"));
    }

    [Fact]
    public async Task Append_RejectsMalformedScenarioId()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Store.AppendAsync("../etc", Row("img_a", "2026-06-22")));
    }

    [Fact]
    public async Task Metrics_NotMistakenForDateBucket()
    {
        // metrics 디렉토리가 ListDatesAsync 의 날짜 목록에 섞이면 안 된다(references·datasets 와 동일).
        var store = Store;
        await store.AppendAsync("demo", Row("img_a", "2026-06-22"));

        var inspectionStore = new FileInspectionStore(new StoragePaths(
            new StorageOptions { DataPath = _dataPath }, AppContext.BaseDirectory));
        var dates = await inspectionStore.ListDatesAsync("demo");
        Assert.DoesNotContain("metrics", dates);
    }
}
