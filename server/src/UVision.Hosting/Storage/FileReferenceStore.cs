using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{DataPath}/{scenarioId}/references/{ok|ng}/{refId}{ext}</c> 파일시스템 기준이미지 저장소.
/// 파일명이 곧 <c>{refId}{ext}</c> — 별도 메타파일 없이 디렉토리 스캔으로 refId·ext 를 얻는다(S-A stem+ext 패턴).
/// </summary>
public sealed class FileReferenceStore : IReferenceStore
{
    private readonly StoragePaths _paths;

    public FileReferenceStore(StoragePaths paths) => _paths = paths;

    public async Task<string> SaveAsync(
        string scenarioId, ReferenceLabel label, ReadOnlyMemory<byte> image, string ext,
        CancellationToken cancellationToken = default)
    {
        var refId = $"ref_{Guid.NewGuid():N}"[..12]; // "ref_" + 8 hex
        var path = _paths.ReferenceFile(scenarioId, label, refId, ext);
        await StoragePaths.AtomicWriteAsync(path, image, cancellationToken);
        return refId;
    }

    public Task<IReadOnlyList<ReferenceInfo>> ListAsync(
        string scenarioId, CancellationToken cancellationToken = default)
    {
        var infos = new List<ReferenceInfo>();
        foreach (var label in new[] { ReferenceLabel.Ok, ReferenceLabel.Ng })
        {
            var dir = _paths.ReferenceDir(scenarioId, label);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(p => p))
            {
                infos.Add(new ReferenceInfo
                {
                    RefId = Path.GetFileNameWithoutExtension(file),
                    Label = label,
                });
            }
        }
        return Task.FromResult<IReadOnlyList<ReferenceInfo>>(infos);
    }

    public async Task<ReferenceBytes?> ReadAsync(
        string scenarioId, ReferenceLabel label, string refId,
        CancellationToken cancellationToken = default)
    {
        var file = FindFile(scenarioId, label, refId);
        if (file is null)
            return null;
        var data = await StoragePaths.ReadAllBytesResilientAsync(file, cancellationToken);
        return new ReferenceBytes(data, ContentTypeOf(file));
    }

    public Task<bool> DeleteAsync(
        string scenarioId, ReferenceLabel label, string refId,
        CancellationToken cancellationToken = default)
    {
        var file = FindFile(scenarioId, label, refId);
        if (file is null)
            return Task.FromResult(false);
        File.Delete(file);
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyList<ReferenceImage>> LoadImagesAsync(
        string scenarioId, IReadOnlyDictionary<string, string> ngLabels, int maxPerLabel,
        CancellationToken cancellationToken = default)
    {
        var images = new List<ReferenceImage>();
        foreach (var label in new[] { ReferenceLabel.Ok, ReferenceLabel.Ng })
        {
            var dir = _paths.ReferenceDir(scenarioId, label);
            if (!Directory.Exists(dir))
                continue;
            var files = Directory.EnumerateFiles(dir).OrderBy(p => p).Take(maxPerLabel);
            foreach (var file in files)
            {
                var refId = Path.GetFileNameWithoutExtension(file);
                images.Add(new ReferenceImage
                {
                    Data = await StoragePaths.ReadAllBytesResilientAsync(file, cancellationToken),
                    Label = label,
                    NgLabel = label == ReferenceLabel.Ng && ngLabels.TryGetValue(refId, out var l)
                        ? l
                        : null,
                    IsPng = Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        return images;
    }

    /// <summary>refId 로 디렉토리에서 매칭 파일을 찾는다(ext 무관). 없으면 null.</summary>
    public async Task<string> SaveClassAsync(
        string scenarioId, string classId, ReadOnlyMemory<byte> image, string ext,
        CancellationToken cancellationToken = default)
    {
        var refId = $"ref_{Guid.NewGuid():N}"[..12];
        await StoragePaths.AtomicWriteAsync(_paths.ReferenceClassFile(scenarioId, classId, refId, ext), image, cancellationToken);
        return refId;
    }

    public Task<IReadOnlyList<ClassReference>> ListClassesAsync(
        string scenarioId, CancellationToken cancellationToken = default)
    {
        var rows = new List<ClassReference>();
        var root = _paths.ReferencesRoot(scenarioId);
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.Ordinal))
                foreach (var file in Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal))
                    rows.Add(new ClassReference(Path.GetFileName(dir), Path.GetFileNameWithoutExtension(file)));
        }
        return Task.FromResult<IReadOnlyList<ClassReference>>(rows);
    }

    public async Task<ReferenceBytes?> ReadClassAsync(
        string scenarioId, string classId, string refId, CancellationToken cancellationToken = default)
    {
        var file = FindClassFile(scenarioId, classId, refId);
        return file is null
            ? null
            : new ReferenceBytes(await StoragePaths.ReadAllBytesResilientAsync(file, cancellationToken), ContentTypeOf(file));
    }

    public Task<bool> DeleteClassAsync(
        string scenarioId, string classId, string refId, CancellationToken cancellationToken = default)
    {
        var file = FindClassFile(scenarioId, classId, refId);
        if (file is null)
            return Task.FromResult(false);
        File.Delete(file);
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyList<ReferenceImage>> LoadClassImagesAsync(
        string scenarioId, IReadOnlyList<ScenarioClass> classes, int maxPerClass,
        CancellationToken cancellationToken = default)
    {
        var images = new List<ReferenceImage>();
        foreach (var cls in classes)
        {
            var dir = _paths.ReferenceClassDir(scenarioId, cls.Id);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal).Take(maxPerClass))
            {
                images.Add(new ReferenceImage
                {
                    Data = await StoragePaths.ReadAllBytesResilientAsync(file, cancellationToken),
                    // Label 은 클래스의 OK/NG 보기 — 프롬프트 표지는 ClassId 가 정한다(선별은 OK/NG 로 부르지 않는다).
                    Label = ScenarioClass.ViewOf(classes, cls.Id) == Verdict.NG ? ReferenceLabel.Ng : ReferenceLabel.Ok,
                    ClassId = cls.Id,
                    IsPng = Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        return images;
    }

    private string? FindClassFile(string scenarioId, string classId, string refId)
    {
        var dir = _paths.ReferenceClassDir(scenarioId, classId);
        if (!Directory.Exists(dir))
            return null;
        var safeId = StoragePaths.Id(refId);
        return Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == safeId);
    }

    private string? FindFile(string scenarioId, ReferenceLabel label, string refId)
    {
        var dir = _paths.ReferenceDir(scenarioId, label);
        if (!Directory.Exists(dir))
            return null;
        var safeId = StoragePaths.Id(refId); // 형식 위반이면 ArgumentException(→400)
        return Directory.EnumerateFiles(dir)
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == safeId);
    }

    private static string ContentTypeOf(string file) =>
        Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";
}
