using System.Globalization;
using System.Text.Json;
using System.Threading;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 파일시스템 모델 레지스트리 — <c>{scenario}/models/{version}/manifest.json</c> + <c>active.json</c>.
/// 경로 지식은 <see cref="StoragePaths"/> 가 소유한다. 버전 디렉터리는 <c>v{n}</c> 단조 증가.
/// </summary>
public sealed class FileModelRegistry : IModelRegistry
{
    private readonly StoragePaths _paths;
    // 채번(read-max → write)·포인터 전이(read → write)의 원자성 보장 — 싱글톤 인스턴스 직렬화.
    // 단일 호스트 단일 프로세스 전제(멀티프로세스 경합은 비대상).
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileModelRegistry(StoragePaths paths) => _paths = paths;

    public async Task<string> RegisterAsync(
        string scenarioId, ModelRegistration registration, CancellationToken ct = default)
    {
        StoragePaths.Id(registration.ModelName); // 화이트리스트 거부 → ArgumentException(→400)
        if (registration.ExportId is not null) StoragePaths.Id(registration.ExportId);

        await _writeLock.WaitAsync(ct);
        try
        {
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
        finally
        {
            _writeLock.Release();
        }
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

    public async Task PromoteAsync(string scenarioId, string version, string by, CancellationToken ct = default)
    {
        // 버전 존재 검증 — 없으면 KeyNotFoundException(→ 엔드포인트 404).
        _ = await ReadManifestAsync(scenarioId, version, ct)
            ?? throw new KeyNotFoundException($"모델 버전 없음: {version}");

        await _writeLock.WaitAsync(ct);
        try
        {
            var current = await ReadPointerAsync(scenarioId, ct);
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // 같은 버전 재격상 시 previous 를 자기 자신으로 만들지 않는다(기존 previous 보존).
            var previous = current is null || current.ActiveVersion == version
                ? current?.PreviousVersion
                : current.ActiveVersion;

            var pointer = new ModelPointer
            {
                ActiveVersion = version,
                PreviousVersion = previous,
                UpdatedAt = now,
                UpdatedBy = by,
            };
            await StoragePaths.AtomicWriteJsonAsync(_paths.ModelPointerFile(scenarioId), pointer, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> RollbackAsync(string scenarioId, string by, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var current = await ReadPointerAsync(scenarioId, ct);
            if (current?.PreviousVersion is null) return false;

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var pointer = new ModelPointer
            {
                ActiveVersion = current.PreviousVersion,
                PreviousVersion = current.ActiveVersion, // 스왑(토글)
                UpdatedAt = now,
                UpdatedBy = by,
            };
            await StoragePaths.AtomicWriteJsonAsync(_paths.ModelPointerFile(scenarioId), pointer, ct);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

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
