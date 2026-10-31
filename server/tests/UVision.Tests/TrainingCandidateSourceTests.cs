using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Label;
using UVision.Api.Services.Training;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>학습 후보는 확정 라벨에서 출발해 짝 판정 레코드 한 건씩만 읽는다 — 라벨 없는 레코드는 열지 않는다.</summary>
public class TrainingCandidateSourceTests : IDisposable
{
    private const string Scenario = "tc";
    private const string Date = "2026-10-31";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvtest_" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FileInspectionStore _inspections;
    private readonly FileLabelStore _labels;

    public TrainingCandidateSourceTests()
    {
        _paths = new StoragePaths(new StorageOptions { DataPath = _root }, _root);
        _inspections = new FileInspectionStore(_paths);
        _labels = new FileLabelStore(_paths);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private Task SaveAsync(string id, bool review = false) => _inspections.SaveAsync(new byte[] { 1, 2, 3 }, "jpg", new StoredResult
    {
        ScenarioId = Scenario, ImageId = id, Verdict = Verdict.OK, Findings = "f", Confidence = 0.9,
        Timestamp = "2026-10-31T10:00:00.0000000Z", ImageFile = id + ".jpg", RequiresReview = review,
    });

    [Fact]
    public async Task Load_JoinsConfirmedLabelsToTheirRecords_InImageIdOrder_AndCountsOrphans()
    {
        await SaveAsync("b", review: true);
        await SaveAsync("a");
        await SaveAsync("unlabeled");
        await SaveAsync("oracle_only");
        await _labels.AppendLabelAsync(Scenario, Date, "b", "NG", "dev", default, LabelStream.Learn);
        await _labels.AppendLabelAsync(Scenario, Date, "a", "OK", "dev");
        await _labels.AppendLabelAsync(Scenario, Date, "gone", "OK", "dev");              // 레코드가 없다 — orphan
        await _labels.AppendOracleAsync(Scenario, Date, "oracle_only", "NG", "oracle");   // 사람 확정 라벨이 아니다 — 후보도 orphan 도 아니다

        var loaded = await new TrainingCandidateSource(_inspections, _labels).LoadAsync(Scenario, default);

        Assert.Equal(["a", "b"], loaded.Items.Select(c => c.ImageId));
        Assert.Equal(1, loaded.Orphans);
        Assert.True(loaded.Items[1].HardNegative);
        Assert.False(loaded.Items[0].HardNegative);
        Assert.Equal(LabelStream.Learn, loaded.Items[1].Stream);
    }
}
