using Microsoft.Extensions.Logging.Abstractions;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Dataset;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 데이터셋 export 상한 — 파생물 정리. 기본(0)은 아무것도 지우지 않고, 켜면 최근 N 개 + 방금 만든 것 + 모델이 가리키는 것만 남는다.
/// </summary>
public sealed class DatasetExportTrimmerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvision-trim-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public DatasetExportTrimmerTests() =>
        _paths = new StoragePaths(new StorageOptions { DataPath = _root }, _root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void MakeExport(string id)
    {
        var dir = _paths.DatasetDir("demo", id);
        Directory.CreateDirectory(Path.Combine(dir, "images", "ok"));
        File.WriteAllBytes(Path.Combine(dir, "images", "ok", "a.jpg"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
    }

    private bool Has(string id) => Directory.Exists(_paths.DatasetDir("demo", id));

    private DatasetExportTrimmer Trimmer(int keep, params string[] trainedOn) =>
        new(_paths, new DirListingExporter(_paths), new FakeRegistry(trainedOn),
            new StorageOptions { DataPath = _root, DatasetExportKeep = keep }, NullLogger<DatasetExportTrimmer>.Instance);

    private void MakeFive()
    {
        foreach (var id in new[] { "20260101-000001-aaaaaa", "20260101-000002-aaaaaa", "20260101-000003-aaaaaa",
                     "20260101-000004-aaaaaa", "20260101-000005-aaaaaa" })
            MakeExport(id);
    }

    [Fact]
    public async Task DefaultKeep_DeletesNothing()
    {
        MakeFive();

        var deleted = await Trimmer(keep: 0).TrimAsync("demo", "20260101-000005-aaaaaa");

        Assert.Equal(0, deleted);
        Assert.True(Has("20260101-000001-aaaaaa") && Has("20260101-000005-aaaaaa"));
    }

    [Fact]
    public async Task Keep_LeavesTheNewestN()
    {
        MakeFive();

        var deleted = await Trimmer(keep: 2).TrimAsync("demo", "20260101-000005-aaaaaa");

        Assert.Equal(3, deleted);
        Assert.True(Has("20260101-000005-aaaaaa") && Has("20260101-000004-aaaaaa"));
        Assert.False(Has("20260101-000003-aaaaaa") || Has("20260101-000002-aaaaaa") || Has("20260101-000001-aaaaaa"));
    }

    [Fact]
    public async Task TheJustCreatedExport_AndTheOnesAModelWasTrainedOn_AreNeverTrimmed()
    {
        MakeFive();

        // 방금 만든 것이 이름순으로는 오래된 쪽이어도(같은 초의 무작위 접미사) 남고, 모델이 배운 가장 오래된 것도 남는다.
        var deleted = await Trimmer(keep: 1, trainedOn: "20260101-000001-aaaaaa")
            .TrimAsync("demo", "20260101-000002-aaaaaa");

        Assert.Equal(2, deleted); // 3 · 4 만 지운다
        Assert.True(Has("20260101-000005-aaaaaa") && Has("20260101-000002-aaaaaa") && Has("20260101-000001-aaaaaa"));
        Assert.False(Has("20260101-000003-aaaaaa") || Has("20260101-000004-aaaaaa"));
    }

    [Fact]
    public async Task OtherScenariosAndIncompleteExports_AreNotTouched()
    {
        MakeFive();
        Directory.CreateDirectory(_paths.DatasetDir("other", "20260101-000001-aaaaaa"));
        File.WriteAllText(Path.Combine(_paths.DatasetDir("other", "20260101-000001-aaaaaa"), "manifest.json"), "{}");
        var unfinished = _paths.DatasetDir("demo", "20260101-000000-aaaaaa"); // manifest 없음 = 만드는 중
        Directory.CreateDirectory(unfinished);

        await Trimmer(keep: 1).TrimAsync("demo", "20260101-000005-aaaaaa");

        Assert.True(Directory.Exists(_paths.DatasetDir("other", "20260101-000001-aaaaaa")));
        Assert.True(Directory.Exists(unfinished));
    }

    /// <summary>manifest 가 있는 export 디렉터리를 이름 역순으로 — 운영 구현과 같은 규칙, 다른 의존 없이.</summary>
    private sealed class DirListingExporter(StoragePaths paths) : IDatasetExporter
    {
        public Task<IReadOnlyList<string>> ListExportsAsync(string scenarioId, CancellationToken cancellationToken = default)
        {
            var dir = paths.DatasetsDir(scenarioId);
            IReadOnlyList<string> ids = !Directory.Exists(dir)
                ? []
                : [.. Directory.EnumerateDirectories(dir)
                    .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                    .Select(d => Path.GetFileName(d)!)
                    .OrderByDescending(n => n, StringComparer.Ordinal)];
            return Task.FromResult(ids);
        }

        public Task<DatasetExportManifest> ExportAsync(string scenarioId, string exportId, ScenarioTraining? rules = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatasetExportManifest?> ReadManifestAsync(string scenarioId, string exportId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRegistry(string[] trainedOn) : IModelRegistry
    {
        public Task<IReadOnlyList<ModelVersionManifest>> ListVersionsAsync(string scenarioId, CancellationToken ct = default)
        {
            IReadOnlyList<ModelVersionManifest> versions =
            [
                .. trainedOn.Select((export, i) => new ModelVersionManifest
                {
                    Version = $"v{i + 1}", ScenarioId = scenarioId, ModelName = $"m{i}", ExportId = export, CreatedAt = "t",
                }),
            ];
            return Task.FromResult(versions);
        }

        public Task<string> RegisterAsync(string scenarioId, ModelRegistration registration, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelVersionManifest?> ReadManifestAsync(string scenarioId, string version, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModelPointer?> ReadPointerAsync(string scenarioId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PromoteAsync(string scenarioId, string version, string by, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RollbackAsync(string scenarioId, string by, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
