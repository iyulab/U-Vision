using UVision.Api.Models;

namespace UVision.Api.Services.Dataset;

/// <summary>
/// 사람 라벨된 검사 이미지를 전용 ML(MLoop 이미지분류) 학습 입력 레이아웃으로 스냅샷한다.
/// 신뢰성 진화 플라이휠 ②(라벨 데이터셋 → 전용 ML 모델 빌드)의 데이터 준비 경계.
/// </summary>
public interface IDatasetExporter
{
    /// <summary>
    /// 시나리오의 모든 사람 라벨(사이드카)을 모아 <c>datasets/{exportId}/images/{class}/</c> 로
    /// 이미지를 복사하고 manifest 를 쓴다. 라벨 없으면 빈 manifest(경고 포함).
    /// </summary>
    Task<DatasetExportManifest> ExportAsync(
        string scenarioId, string exportId, CancellationToken cancellationToken = default);

    /// <summary>시나리오의 export id 목록(최신 먼저). 없으면 빈 리스트.</summary>
    Task<IReadOnlyList<string>> ListExportsAsync(
        string scenarioId, CancellationToken cancellationToken = default);

    /// <summary>export manifest 를 읽는다. 없으면 null.</summary>
    Task<DatasetExportManifest?> ReadManifestAsync(
        string scenarioId, string exportId, CancellationToken cancellationToken = default);
}
