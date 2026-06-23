using System.Globalization;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Services.Dataset;

/// <summary>
/// 파일시스템 데이터셋 export. 진실원천 = 사람 라벨 사이드카(<see cref="ILabelStore"/>).
/// 라벨된 image_id 마다 저장 이미지(<see cref="IInspectionStore"/>)를 클래스 폴더로 복사한다.
/// 결과 레코드·라벨 사이드카는 건드리지 않는다(읽기 전용 소비 + 파생 스냅샷).
/// </summary>
public sealed class FileDatasetExporter : IDatasetExporter
{
    /// <summary>MLoop 이미지분류 권장 클래스당 최소 표본 수(미만이면 학습 품질 경고).</summary>
    public const int MinPerClassRecommended = 5;

    private readonly StoragePaths _paths;
    private readonly IInspectionStore _inspections;
    private readonly ILabelStore _labels;

    public FileDatasetExporter(StoragePaths paths, IInspectionStore inspections, ILabelStore labels)
    {
        _paths = paths;
        _inspections = inspections;
        _labels = labels;
    }

    public async Task<DatasetExportManifest> ExportAsync(
        string scenarioId, string exportId, CancellationToken cancellationToken = default)
    {
        // 경로 형식 위반(scenarioId/exportId)은 여기서 ArgumentException → 엔드포인트 400.
        var manifestPath = _paths.DatasetManifest(scenarioId, exportId);

        var items = new List<DatasetItem>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var missing = 0;

        var dates = await _inspections.ListDatesAsync(scenarioId, cancellationToken);
        foreach (var date in dates)
        {
            var labels = await _labels.ListAsync(scenarioId, date, cancellationToken);
            foreach (var label in labels)
            {
                var image = await _inspections.ReadImageAsync(
                    scenarioId, date, label.ImageId, cancellationToken);
                if (image is null)
                {
                    // 라벨 사이드카는 있으나 짝 이미지가 없는 orphan — 스킵, 경고로 집계.
                    missing++;
                    continue;
                }

                // operative 라벨이 없으면(oracle-only 또는 미라벨) 데이터셋에서 제외.
                var operative = label.OperativeLabel;
                if (operative is null)
                {
                    missing++;
                    continue;
                }

                var classDir = operative.ToLowerInvariant();
                var ext = ExtOf(image.ContentType);
                var dest = Path.Combine(
                    _paths.DatasetClassDir(scenarioId, exportId, classDir), label.ImageId + ext);
                await StoragePaths.AtomicWriteAsync(dest, image.Data, cancellationToken);

                items.Add(new DatasetItem
                {
                    ImageId = label.ImageId,
                    Date = date,
                    Label = operative,
                    ClassDir = classDir,
                    ImageFile = $"images/{classDir}/{label.ImageId}{ext}",
                });
                counts[classDir] = counts.GetValueOrDefault(classDir) + 1;
            }
        }

        // 결정론적 출력 — 날짜·image_id 순.
        items.Sort((a, b) =>
        {
            var d = string.CompareOrdinal(a.Date, b.Date);
            return d != 0 ? d : string.CompareOrdinal(a.ImageId, b.ImageId);
        });

        var classes = counts
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new DatasetClassCount { ClassDir = kv.Key, Count = kv.Value })
            .ToList();

        var warnings = BuildWarnings(classes, missing);

        var manifest = new DatasetExportManifest
        {
            ExportId = exportId,
            ScenarioId = scenarioId,
            CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ImagesRoot = $"datasets/{exportId}/images",
            Total = items.Count,
            Classes = classes,
            Warnings = warnings,
            Items = items,
        };

        await StoragePaths.AtomicWriteJsonAsync(manifestPath, manifest, cancellationToken);
        return manifest;
    }

    public Task<IReadOnlyList<string>> ListExportsAsync(
        string scenarioId, CancellationToken cancellationToken = default)
    {
        var dir = _paths.DatasetsDir(scenarioId); // 형식 위반 → ArgumentException(→400)
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>([]);

        // manifest.json 이 있는 export 만(완결된 것), 이름 역순(타임스탬프 id → 최신 먼저).
        var ids = Directory.EnumerateDirectories(dir)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(ids!);
    }

    public async Task<DatasetExportManifest?> ReadManifestAsync(
        string scenarioId, string exportId, CancellationToken cancellationToken = default)
    {
        var path = _paths.DatasetManifest(scenarioId, exportId); // 형식 위반 → ArgumentException(→400)
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<DatasetExportManifest>(
            stream, StoragePaths.Json, cancellationToken);
    }

    private static List<string> BuildWarnings(IReadOnlyList<DatasetClassCount> classes, int missing)
    {
        var warnings = new List<string>();
        if (classes.Count == 0)
            warnings.Add("라벨된 이미지가 없습니다 — export 가 비어 있습니다.");
        else if (classes.Count < 2)
            warnings.Add($"클래스가 1개뿐입니다('{classes[0].ClassDir}') — 이진 분류 학습에는 최소 2개 클래스가 필요합니다.");

        foreach (var c in classes.Where(c => c.Count < MinPerClassRecommended))
            warnings.Add($"클래스 '{c.ClassDir}' 표본 {c.Count}장 — 권장 최소 {MinPerClassRecommended}장 미만(학습 품질 저하 가능).");

        if (missing > 0)
            warnings.Add($"라벨 {missing}건에 대응하는 이미지가 없어 제외했습니다.");

        return warnings;
    }

    private static string ExtOf(string contentType) =>
        contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
}
