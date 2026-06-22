using System.Globalization;
using System.Text.Json;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 파일시스템 모델 레지스트리 — <c>{scenario}/models/{version}/manifest.json</c> + <c>active.json</c>.
/// 경로 지식은 <see cref="StoragePaths"/> 가 소유한다. 버전 디렉터리는 <c>v{n}</c> 단조 증가.
/// </summary>
public sealed class FileModelRegistry : IModelRegistry
{
    private readonly StoragePaths _paths;

    public FileModelRegistry(StoragePaths paths) => _paths = paths;

    public async Task<string> RegisterAsync(
        string scenarioId, ModelRegistration registration, CancellationToken ct = default)
    {
        StoragePaths.Id(registration.ModelName); // 화이트리스트 거부 → ArgumentException(→400)
        if (registration.ExportId is not null) StoragePaths.Id(registration.ExportId);

        var modelsDir = _paths.ModelsDir(scenarioId); // scenarioId 검증
        var version = "v" + NextVersionNumber(modelsDir);
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        var manifest = new ModelVersionManifest
        {
            Version = version,
            ScenarioId = scenarioId,
            ModelName = registration.ModelName,
            Endpoint = registration.Endpoint,
            ExportId = registration.ExportId,
            Metrics = registration.Metrics,
            CreatedAt = now,
            CreatedBy = registration.By,
            Note = registration.Note,
        };
        await StoragePaths.AtomicWriteJsonAsync(_paths.ModelManifest(scenarioId, version), manifest, ct);
        return version;
    }

    public async Task<IReadOnlyList<ModelVersionManifest>> ListVersionsAsync(
        string scenarioId, CancellationToken ct = default)
    {
        var modelsDir = _paths.ModelsDir(scenarioId);
        var result = new List<ModelVersionManifest>();
        if (!Directory.Exists(modelsDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(modelsDir))
        {
            var version = Path.GetFileName(dir);
            if (!IsVersionDir(version)) continue;
            var manifest = await ReadManifestAsync(scenarioId, version, ct);
            if (manifest is not null) result.Add(manifest);
        }
        return result.OrderBy(m => VersionNumber(m.Version)).ToList();
    }

    public async Task<ModelVersionManifest?> ReadManifestAsync(
        string scenarioId, string version, CancellationToken ct = default)
    {
        var path = _paths.ModelManifest(scenarioId, version); // id/version 검증
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ModelVersionManifest>(
            stream, StoragePaths.Json, ct);
    }

    public async Task<ModelPointer?> ReadPointerAsync(string scenarioId, CancellationToken ct = default)
    {
        var path = _paths.ModelPointerFile(scenarioId); // scenarioId 검증
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ModelPointer>(stream, StoragePaths.Json, ct);
    }

    public Task PromoteAsync(string scenarioId, string version, string by, CancellationToken ct = default) =>
        throw new NotImplementedException(); // Task 4

    public Task<bool> RollbackAsync(string scenarioId, string by, CancellationToken ct = default) =>
        throw new NotImplementedException(); // Task 4

    private static int NextVersionNumber(string modelsDir)
    {
        if (!Directory.Exists(modelsDir)) return 1;
        var max = 0;
        foreach (var dir in Directory.EnumerateDirectories(modelsDir))
        {
            var name = Path.GetFileName(dir);
            if (IsVersionDir(name) && VersionNumber(name) > max) max = VersionNumber(name);
        }
        return max + 1;
    }

    private static bool IsVersionDir(string name) =>
        name.Length > 1 && name[0] == 'v' && int.TryParse(name.AsSpan(1), out _);

    private static int VersionNumber(string version) =>
        int.TryParse(version.AsSpan(1), out var n) ? n : 0;
}
