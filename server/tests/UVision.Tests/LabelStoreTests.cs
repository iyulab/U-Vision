using System;
using UVision.Api.Configuration;
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
}
