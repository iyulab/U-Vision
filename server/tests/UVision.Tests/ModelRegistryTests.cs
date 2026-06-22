using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

public sealed class ModelRegistryTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), "uv-reg-" + Guid.NewGuid().ToString("N"));

    private FileModelRegistry NewRegistry() =>
        new(new StoragePaths(new StorageOptions { DataPath = _dataPath }, AppContext.BaseDirectory));

    [Fact]
    public async Task Register_MintsSequentialVersions()
    {
        var reg = NewRegistry();
        var v1 = await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "chromate-a" });
        var v2 = await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "chromate-b" });

        Assert.Equal("v1", v1);
        Assert.Equal("v2", v2);
    }

    [Fact]
    public async Task Register_WritesManifestWithProvenance()
    {
        var reg = NewRegistry();
        var v = await reg.RegisterAsync("chromate", new ModelRegistration
        {
            ModelName = "chromate-a", ExportId = "exp_1", By = "device-x",
            Metrics = new Dictionary<string, double> { ["ng_recall"] = 0.97 },
        });

        var manifest = await reg.ReadManifestAsync("chromate", v);
        Assert.NotNull(manifest);
        Assert.Equal("chromate-a", manifest!.ModelName);
        Assert.Equal("exp_1", manifest.ExportId);
        Assert.Equal("device-x", manifest.CreatedBy);
        Assert.Equal(0.97, manifest.Metrics!["ng_recall"]);
    }

    [Fact]
    public async Task Register_DoesNotTouchActivePointer()
    {
        var reg = NewRegistry();
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "chromate-a" });
        Assert.Null(await reg.ReadPointerAsync("chromate"));
    }

    [Fact]
    public async Task Register_RejectsInvalidModelName()
    {
        var reg = NewRegistry();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "../escape" }));
    }

    [Fact]
    public async Task ListVersions_ReturnsAllAscending()
    {
        var reg = NewRegistry();
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "a" });
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "b" });

        var versions = await reg.ListVersionsAsync("chromate");
        Assert.Equal(new[] { "v1", "v2" }, versions.Select(m => m.Version).ToArray());
    }

    [Fact]
    public async Task ListVersions_EmptyWhenNone() =>
        Assert.Empty(await NewRegistry().ListVersionsAsync("chromate"));

    [Fact]
    public async Task Promote_SetsActiveAndRecordsPrevious()
    {
        var reg = NewRegistry();
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "a" }); // v1
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "b" }); // v2

        await reg.PromoteAsync("chromate", "v1", "device-x");
        await reg.PromoteAsync("chromate", "v2", "device-x");

        var pointer = await reg.ReadPointerAsync("chromate");
        Assert.Equal("v2", pointer!.ActiveVersion);
        Assert.Equal("v1", pointer.PreviousVersion);
    }

    [Fact]
    public async Task Promote_UnknownVersion_Throws()
    {
        var reg = NewRegistry();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reg.PromoteAsync("chromate", "v9", "x"));
    }

    [Fact]
    public async Task Rollback_SwapsActiveAndPrevious()
    {
        var reg = NewRegistry();
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "a" }); // v1
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "b" }); // v2
        await reg.PromoteAsync("chromate", "v1", "x");
        await reg.PromoteAsync("chromate", "v2", "x"); // active=v2, prev=v1

        var rolled = await reg.RollbackAsync("chromate", "x");
        var pointer = await reg.ReadPointerAsync("chromate");

        Assert.True(rolled);
        Assert.Equal("v1", pointer!.ActiveVersion);
        Assert.Equal("v2", pointer.PreviousVersion);
    }

    [Fact]
    public async Task Rollback_TwiceReturnsToOriginal()
    {
        var reg = NewRegistry();
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "a" });
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "b" });
        await reg.PromoteAsync("chromate", "v1", "x");
        await reg.PromoteAsync("chromate", "v2", "x");

        await reg.RollbackAsync("chromate", "x"); // active=v1
        await reg.RollbackAsync("chromate", "x"); // active=v2 (토글 원복)

        var pointer = await reg.ReadPointerAsync("chromate");
        Assert.Equal("v2", pointer!.ActiveVersion);
    }

    [Fact]
    public async Task Rollback_NoPrevious_ReturnsFalse()
    {
        var reg = NewRegistry();
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "a" });
        await reg.PromoteAsync("chromate", "v1", "x"); // active=v1, prev=null

        Assert.False(await reg.RollbackAsync("chromate", "x"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }
}
