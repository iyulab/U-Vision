using UVision.Api.Configuration;
using UVision.Api.Storage;

namespace UVision.Api.Services.Dataset;

/// <summary>
/// 데이터셋 export 상한(<see cref="StorageOptions.DatasetExportKeep"/>) — export 를 새로 만든 뒤 오래된 것을 정리한다. export 는 라벨 정본에서
/// 언제든 다시 만들 수 있는 <b>파생물</b>이라 설정만으로 자동 정리해도 된다(정본 삭제는 <c>retention/purge</c> 의 명시 행위뿐).
/// <list type="bullet">
/// <item>기본(0)은 지우지 않는다.</item>
/// <item>방금 만든 export 와, 모델 레지스트리의 어느 버전이든 <c>export_id</c> 로 가리키는 export 는 상한과 무관하게 남긴다 —
/// 모델이 어느 데이터셋을 배웠는지(학습 준비 신호의 근거)를 잃지 않는다.</item>
/// <item>정리는 집안일이라 실패해도 export 요청을 실패시키지 않는다(로그만).</item>
/// </list>
/// </summary>
public sealed class DatasetExportTrimmer(
    StoragePaths paths, IDatasetExporter exporter, IModelRegistry registry, StorageOptions options,
    ILogger<DatasetExportTrimmer> logger)
{
    /// <returns>지운 export 수.</returns>
    public async Task<int> TrimAsync(string scenarioId, string justCreatedExportId, CancellationToken ct = default)
    {
        if (options.DatasetExportKeep <= 0)
            return 0;
        var deleted = 0;
        try
        {
            var newestFirst = await exporter.ListExportsAsync(scenarioId, ct);
            var keep = newestFirst.Take(options.DatasetExportKeep).ToHashSet(StringComparer.Ordinal);
            keep.Add(justCreatedExportId);
            foreach (var version in await registry.ListVersionsAsync(scenarioId, ct))
                if (version.ExportId is { } trainedOn)
                    keep.Add(trainedOn);

            foreach (var id in newestFirst.Where(id => !keep.Contains(id)))
            {
                Directory.Delete(paths.DatasetDir(scenarioId, id), recursive: true);
                deleted++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "데이터셋 export 정리 실패 — 상한만 못 지켰다 (scenario={ScenarioId})", scenarioId);
        }
        if (deleted > 0)
            logger.LogInformation(
                "데이터셋 export {Deleted}개 정리 (scenario={ScenarioId}, 상한 {Keep})", deleted, scenarioId, options.DatasetExportKeep);
        return deleted;
    }
}
