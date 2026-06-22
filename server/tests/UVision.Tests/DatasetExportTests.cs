using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Dataset;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// FW-1 — 사람 라벨 사이드카 → MLoop 이미지분류 입력 레이아웃 export.
/// 신뢰성 플라이휠 ②(전용 ML 빌드)의 데이터 준비 단계 검증.
/// </summary>
public class DatasetExportTests
{
    private const string Scenario = "demo";
    private const string ExportId = "exp-test";

    private static (StoragePaths paths, FileInspectionStore ins, FileLabelStore lab) NewStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvision-ds-" + Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(new StorageOptions { DataPath = root }, AppContext.BaseDirectory);
        return (paths, new FileInspectionStore(paths), new FileLabelStore(paths));
    }

    /// <summary>검사 이미지 + 사람 라벨을 함께 seed.</summary>
    private static async Task SeedLabeled(
        FileInspectionStore ins, FileLabelStore lab,
        string date, string imageId, string label)
    {
        var ts = date + "T00:00:00Z";
        await ins.SaveAsync(new byte[] { 1, 2, 3 }, ".jpg", new StoredResult
        {
            ScenarioId = Scenario, ImageId = imageId, Verdict = Verdict.OK,
            Findings = "", Confidence = 0.9, Timestamp = ts, ImageFile = imageId + ".jpg",
        });
        await lab.WriteAsync(Scenario, date, new StoredLabel
        { ImageId = imageId, Label = label, Timestamp = ts });
    }

    [Fact]
    public async Task Export_CopiesLabeledImages_IntoClassDirs()
    {
        var (paths, ins, lab) = NewStores();
        await SeedLabeled(ins, lab, "2026-06-10", "img_a", "OK");
        await SeedLabeled(ins, lab, "2026-06-10", "img_b", "OK");
        await SeedLabeled(ins, lab, "2026-06-11", "img_c", "NG");
        var exporter = new FileDatasetExporter(paths, ins, lab);

        var manifest = await exporter.ExportAsync(Scenario, ExportId);

        Assert.Equal(3, manifest.Total);
        Assert.Equal(2, manifest.Classes.Single(c => c.ClassDir == "ok").Count);
        Assert.Equal(1, manifest.Classes.Single(c => c.ClassDir == "ng").Count);

        // 디스크에 클래스 폴더로 실제 복사됐는가.
        Assert.True(File.Exists(Path.Combine(paths.DatasetClassDir(Scenario, ExportId, "ok"), "img_a.jpg")));
        Assert.True(File.Exists(Path.Combine(paths.DatasetClassDir(Scenario, ExportId, "ng"), "img_c.jpg")));
        Assert.True(File.Exists(paths.DatasetManifest(Scenario, ExportId)));
    }

    [Fact]
    public async Task Export_ExcludesUnlabeledImages()
    {
        var (paths, ins, lab) = NewStores();
        await SeedLabeled(ins, lab, "2026-06-10", "img_labeled", "OK");
        // 라벨 없는 검사 이미지 — export 에서 제외돼야 한다.
        await ins.SaveAsync(new byte[] { 9 }, ".jpg", new StoredResult
        {
            ScenarioId = Scenario, ImageId = "img_unlabeled", Verdict = Verdict.NG,
            Findings = "", Confidence = 0.5, Timestamp = "2026-06-10T01:00:00Z",
            ImageFile = "img_unlabeled.jpg",
        });
        var exporter = new FileDatasetExporter(paths, ins, lab);

        var manifest = await exporter.ExportAsync(Scenario, ExportId);

        Assert.Equal(1, manifest.Total);
        Assert.DoesNotContain(manifest.Items, i => i.ImageId == "img_unlabeled");
    }

    [Fact]
    public async Task Export_WarnsOnSingleClass()
    {
        var (paths, ins, lab) = NewStores();
        for (var i = 0; i < 6; i++) // 권장 최소 충족, 단일 클래스만 트리거하도록
            await SeedLabeled(ins, lab, "2026-06-10", $"img_{i}", "OK");
        var exporter = new FileDatasetExporter(paths, ins, lab);

        var manifest = await exporter.ExportAsync(Scenario, ExportId);

        Assert.Contains(manifest.Warnings, w => w.Contains("클래스가 1개"));
    }

    [Fact]
    public async Task Export_WarnsBelowMinPerClass()
    {
        var (paths, ins, lab) = NewStores();
        await SeedLabeled(ins, lab, "2026-06-10", "img_a", "OK");
        await SeedLabeled(ins, lab, "2026-06-10", "img_b", "NG");
        var exporter = new FileDatasetExporter(paths, ins, lab);

        var manifest = await exporter.ExportAsync(Scenario, ExportId);

        Assert.Contains(manifest.Warnings, w => w.Contains("권장 최소"));
    }

    [Fact]
    public async Task Export_SkipsLabelWithoutImage_AndWarns()
    {
        var (paths, ins, lab) = NewStores();
        await SeedLabeled(ins, lab, "2026-06-10", "img_real", "OK");
        // 이미지 없이 라벨 사이드카만 — orphan.
        await lab.WriteAsync(Scenario, "2026-06-10", new StoredLabel
        { ImageId = "img_orphan", Label = "NG", Timestamp = "2026-06-10T00:00:00Z" });
        var exporter = new FileDatasetExporter(paths, ins, lab);

        var manifest = await exporter.ExportAsync(Scenario, ExportId);

        Assert.Equal(1, manifest.Total);
        Assert.DoesNotContain(manifest.Items, i => i.ImageId == "img_orphan");
        Assert.Contains(manifest.Warnings, w => w.Contains("이미지가 없어"));
    }

    [Fact]
    public async Task Export_EmptyWhenNoLabels()
    {
        var (paths, ins, lab) = NewStores();
        var exporter = new FileDatasetExporter(paths, ins, lab);

        var manifest = await exporter.ExportAsync(Scenario, ExportId);

        Assert.Equal(0, manifest.Total);
        Assert.Empty(manifest.Items);
        Assert.Contains(manifest.Warnings, w => w.Contains("라벨된 이미지가 없"));
    }

    [Fact]
    public async Task Export_ManifestRoundTripsOnDisk()
    {
        var (paths, ins, lab) = NewStores();
        await SeedLabeled(ins, lab, "2026-06-10", "img_a", "OK");
        var exporter = new FileDatasetExporter(paths, ins, lab);

        var manifest = await exporter.ExportAsync(Scenario, ExportId);

        var json = await File.ReadAllTextAsync(paths.DatasetManifest(Scenario, ExportId));
        Assert.Contains("\"export_id\"", json); // snake_case wire 정책 보존
        Assert.Contains("images/ok/img_a.jpg", json);
        Assert.Equal("datasets/exp-test/images", manifest.ImagesRoot);
    }

    [Fact]
    public async Task Export_RejectsMalformedExportId()
    {
        var (paths, ins, lab) = NewStores();
        var exporter = new FileDatasetExporter(paths, ins, lab);

        await Assert.ThrowsAsync<ArgumentException>(
            () => exporter.ExportAsync(Scenario, "../escape"));
    }
}
