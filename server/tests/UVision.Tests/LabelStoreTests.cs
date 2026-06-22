using System;
using UVision.Api.Configuration;
using UVision.Api.Services.Label;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>FileLabelStore 사이드카 CRUD + StoragePaths.LabelJson sanitize.</summary>
public class LabelStoreTests
{
    private static StoragePaths NewPaths(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "uvision-label-" + Guid.NewGuid().ToString("N"));
        return new StoragePaths(new StorageOptions { DataPath = root }, AppContext.BaseDirectory);
    }

    [Fact]
    public void LabelJson_PlacesSidecarNextToResult()
    {
        var paths = NewPaths(out _);
        var label = paths.LabelJson("demo", "2026-06-09", "img_abc");
        var result = paths.ResultFile("demo", "2026-06-09", "img_abc");

        Assert.Equal(Path.GetDirectoryName(result), Path.GetDirectoryName(label));
        Assert.EndsWith("img_abc.label.json", label);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("bad/id")]
    public void LabelJson_RejectsMalformedImageId(string imageId)
    {
        var paths = NewPaths(out _);
        Assert.Throws<ArgumentException>(() => paths.LabelJson("demo", "2026-06-09", imageId));
    }

    [Fact]
    public void LabelJson_RejectsMalformedDate()
    {
        var paths = NewPaths(out _);
        Assert.Throws<ArgumentException>(() => paths.LabelJson("demo", "not-a-date", "img_abc"));
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);

        await store.AppendLabelAsync("demo", "2026-06-09", "img_abc", "NG", "dev");
        var read = await store.ReadAsync("demo", "2026-06-09", "img_abc");

        Assert.NotNull(read);
        Assert.Equal("NG", read!.Label);
        Assert.Equal("img_abc", read.ImageId);
    }

    [Fact]
    public async Task AppendLabel_LastIsOperative()
    {
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);
        await store.AppendLabelAsync("demo", "2026-06-09", "img_abc", "OK", "dev");
        await store.AppendLabelAsync("demo", "2026-06-09", "img_abc", "NG", "dev");

        var read = await store.ReadAsync("demo", "2026-06-09", "img_abc");
        Assert.Equal("NG", read!.Label); // 마지막 쓰기가 이긴다
    }

    [Fact]
    public async Task Read_ReturnsNull_WhenAbsent()
    {
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);
        Assert.Null(await store.ReadAsync("demo", "2026-06-09", "img_missing"));
    }

    [Fact]
    public async Task Delete_RemovesSidecar_AndIsNoopWhenAbsent()
    {
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);
        await store.AppendLabelAsync("demo", "2026-06-09", "img_abc", "OK", "dev");

        await store.DeleteAsync("demo", "2026-06-09", "img_abc");
        Assert.Null(await store.ReadAsync("demo", "2026-06-09", "img_abc"));

        // 없는 것 삭제 = no-op(예외 없음).
        await store.DeleteAsync("demo", "2026-06-09", "img_abc");
    }

    [Fact]
    public async Task List_ReturnsAllLabelsForDate()
    {
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);
        await store.AppendLabelAsync("demo", "2026-06-09", "img_a", "OK", "dev");
        await store.AppendLabelAsync("demo", "2026-06-09", "img_b", "NG", "dev");

        var all = await store.ListAsync("demo", "2026-06-09");
        Assert.Equal(2, all.Count);
        Assert.Contains(all, l => l.ImageId == "img_a" && l.Label == "OK");
        Assert.Contains(all, l => l.ImageId == "img_b" && l.Label == "NG");
    }

    [Fact]
    public async Task Store_IsClassAgnostic_RoundTripsArbitraryLabel()
    {
        // 저장소는 라벨을 string 으로 보존한다 — 이진(OK/NG)에 묶이지 않는다.
        // 다중분류 확장 시 허용집합(LabelSet)만 넓히면 저장/조회는 그대로 동작함을 증명.
        // (v1 의 OK/NG 강제는 엔드포인트 책임이지 저장소 책임이 아니다.)
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);
        await store.AppendLabelAsync("demo", "2026-06-09", "img_multi", "SCRATCH_A", "dev");

        var read = await store.ReadAsync("demo", "2026-06-09", "img_multi");
        Assert.Equal("SCRATCH_A", read!.Label); // 임의 클래스 문자열 무손실 왕복
    }

    [Fact]
    public async Task AppendLabel_AccumulatesHistory_OperativeIsLast()
    {
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);
        await store.AppendLabelAsync("demo", "2026-06-09", "img_a", "OK", "dev");
        await store.AppendLabelAsync("demo", "2026-06-09", "img_a", "NG", "dev");

        var read = await store.ReadAsync("demo", "2026-06-09", "img_a");
        Assert.Equal("NG", read!.Label);          // 최신 operative
        Assert.Equal(2, read.History!.Count);     // 이력 누적(덮어쓰지 않음)
    }

    [Fact]
    public async Task AppendAudit_SameLabel_IsConsistent_OperativeUnchanged()
    {
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);
        await store.AppendLabelAsync("demo", "2026-06-09", "img_a", "NG", "dev");

        var outcome = await store.AppendAuditAsync("demo", "2026-06-09", "img_a", "NG", "dev");

        Assert.Equal(LabelAuditStatus.Consistent, outcome.Status);
        Assert.Equal("NG", outcome.PriorLabel);
        var read = await store.ReadAsync("demo", "2026-06-09", "img_a");
        Assert.Equal("NG", read!.Label);                          // operative 불변
        Assert.Equal(LabelAuditStatus.Consistent, read.Audit!.Status);
        Assert.Equal(2, read.History!.Count);                     // label + audit
        Assert.Equal(LabelMode.Audit, read.History[1].Mode);
    }

    [Fact]
    public async Task AppendAudit_DifferentLabel_IsConflicted_ThenResolvedByLabel()
    {
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);
        await store.AppendLabelAsync("demo", "2026-06-09", "img_a", "OK", "dev");

        var outcome = await store.AppendAuditAsync("demo", "2026-06-09", "img_a", "NG", "dev");
        Assert.Equal(LabelAuditStatus.Conflicted, outcome.Status);
        Assert.Equal("OK", outcome.PriorLabel);                   // 블라인드였던 직전 라벨 공개

        // 사람 해소 = 일반 라벨 쓰기 → resolved.
        await store.AppendLabelAsync("demo", "2026-06-09", "img_a", "NG", "dev");
        var read = await store.ReadAsync("demo", "2026-06-09", "img_a");
        Assert.Equal("NG", read!.Label);
        Assert.Equal(LabelAuditStatus.Resolved, read.Audit!.Status);
    }

    [Fact]
    public async Task AppendAudit_UnlabeledImage_Throws()
    {
        var paths = NewPaths(out _);
        var store = new FileLabelStore(paths);
        // 라벨 없는 이미지는 감사 대상이 아님.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAuditAsync("demo", "2026-06-09", "img_missing", "NG", "dev"));
    }

    [Fact]
    public async Task ReadAsync_LegacySidecar_NormalizesHistory()
    {
        // 구 사이드카를 직접 디스크에 쓴 뒤 읽으면 history 가 합성된다.
        var paths = NewPaths(out _);
        var path = paths.LabelJson("demo", "2026-06-09", "img_legacy");
        await StoragePaths.AtomicWriteJsonAsync(path,
            new UVision.Api.Models.StoredLabel { ImageId = "img_legacy", Label = "NG", Timestamp = "2026-06-09T00:00:00Z" });

        var store = new FileLabelStore(paths);
        var read = await store.ReadAsync("demo", "2026-06-09", "img_legacy");
        Assert.Single(read!.History!);
        Assert.Equal(LabelAuditStatus.Unaudited, read.Audit!.Status);
    }
}
