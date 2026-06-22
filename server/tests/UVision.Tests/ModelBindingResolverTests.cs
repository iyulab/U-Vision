using Microsoft.Extensions.Logging.Abstractions;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Models;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

public sealed class ModelBindingResolverTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), "uv-res-" + Guid.NewGuid().ToString("N"));

    private (FileModelRegistry reg, ModelBindingResolver resolver) New()
    {
        var paths = new StoragePaths(new StorageOptions { DataPath = _dataPath }, AppContext.BaseDirectory);
        var reg = new FileModelRegistry(paths);
        return (reg, new ModelBindingResolver(reg, NullLogger<ModelBindingResolver>.Instance));
    }

    [Fact]
    public async Task Resolve_ReturnsActiveBinding()
    {
        var (reg, resolver) = New();
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "chromate-a" }); // v1
        await reg.PromoteAsync("chromate", "v1", "x");

        var binding = await resolver.ResolveAsync("chromate");
        Assert.NotNull(binding);
        Assert.Equal("v1", binding!.Version);
        Assert.Equal("chromate-a", binding.ModelName);
    }

    [Fact]
    public async Task Resolve_NoPointer_ReturnsNull()
    {
        var (reg, resolver) = New();
        await reg.RegisterAsync("chromate", new ModelRegistration { ModelName = "chromate-a" }); // 등록만, 미격상
        Assert.Null(await resolver.ResolveAsync("chromate"));
    }

    [Fact]
    public async Task Resolve_DanglingPointer_ReturnsNull()
    {
        var paths = new StoragePaths(new StorageOptions { DataPath = _dataPath }, AppContext.BaseDirectory);
        // 존재하지 않는 버전을 가리키는 포인터를 직접 기록.
        await StoragePaths.AtomicWriteJsonAsync(paths.ModelPointerFile("chromate"), new ModelPointer
        {
            ActiveVersion = "v9", PreviousVersion = null, UpdatedAt = "2026-06-22T00:00:00Z", UpdatedBy = "x",
        });
        var resolver = new ModelBindingResolver(new FileModelRegistry(paths), NullLogger<ModelBindingResolver>.Instance);
        Assert.Null(await resolver.ResolveAsync("chromate"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }
}
