using System.Globalization;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Services.Oracle;

/// <summary>
/// 오라클 스윕 1회 — requires_review 결과 중 미오라클 건을 찾아 2차 소견을 사이드카에 append(④-B).
/// 핫패스 밖·degrade-safe(per-item 예외 흡수). 타이머는 OracleSweepBackgroundService 소유(분리).
/// </summary>
public sealed class OracleSweepService
{
    private readonly IOracleProvider _oracle;
    private readonly IScenarioStore _scenarios;
    private readonly IInspectionStore _inspections;
    private readonly IReferenceStore _references;
    private readonly ILabelStore _labels;
    private readonly OracleOptions _options;
    private readonly ILogger _log;

    public OracleSweepService(
        IOracleProvider oracle, IScenarioStore scenarios, IInspectionStore inspections,
        IReferenceStore references, ILabelStore labels, OracleOptions options, ILoggerFactory loggerFactory)
    {
        _oracle = oracle; _scenarios = scenarios; _inspections = inspections;
        _references = references; _labels = labels; _options = options;
        _log = loggerFactory.CreateLogger<OracleSweepService>();
    }

    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        if (!_oracle.IsEnabled)
            return 0;

        var processed = 0;
        var scenarios = await _scenarios.ListAsync(ct);
        foreach (var scenario in scenarios)
        {
            if (processed >= _options.BatchCap)
                break;
            if (!OracleSweepPlanner.EgressAllowed(_oracle.IsCloud, scenario.AllowCloudEgress))
                continue;

            foreach (var date in RecentDates(_options.LookbackDays))
            {
                if (processed >= _options.BatchCap)
                    break;

                IReadOnlyList<StoredResult> results;
                try { results = await _inspections.ListAsync(scenario.ScenarioId, date, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "결과 목록 실패 (sc={Sc}, date={Date})", scenario.ScenarioId, date); continue; }

                foreach (var r in results)
                {
                    if (processed >= _options.BatchCap)
                        break;

                    StoredLabel? sidecar;
                    try { sidecar = await _labels.ReadAsync(scenario.ScenarioId, date, r.ImageId, ct); }
                    catch (Exception ex) { _log.LogWarning(ex, "사이드카 읽기 실패 (img={Img})", r.ImageId); continue; }

                    if (!OracleSweepPlanner.NeedsOracle(r, sidecar))
                        continue;

                    try
                    {
                        await ProcessAsync(scenario, date, r.ImageId, ct);
                        processed++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "오라클 처리 실패 — 다음 항목 계속 (sc={Sc}, img={Img})",
                            scenario.ScenarioId, r.ImageId);
                    }
                }
            }
        }
        return processed;
    }

    private async Task ProcessAsync(Scenario scenario, string date, string imageId, CancellationToken ct)
    {
        var img = await _inspections.ReadImageAsync(scenario.ScenarioId, date, imageId, ct)
            ?? throw new InvalidOperationException($"이미지 없음: {imageId}");

        IReadOnlyList<ReferenceImage> refs = [];
        try { refs = await _references.LoadImagesAsync(scenario.ScenarioId, scenario.NgLabels, Math.Max(0, scenario.ReferenceCap), ct); }
        catch (Exception ex) { _log.LogWarning(ex, "기준이미지 로드 실패 — refs 없이 (sc={Sc})", scenario.ScenarioId); }

        var context = new ScenarioContext
        {
            ScenarioId = scenario.ScenarioId, Name = scenario.Name, Criteria = scenario.Criteria, References = refs,
        };
        var opinion = await _oracle.SecondOpinionAsync(img.Data, context, ct);
        await _labels.AppendOracleAsync(scenario.ScenarioId, date, imageId, opinion.Verdict.ToString(), "oracle", ct);
        _log.LogInformation("오라클 소견 기록 (sc={Sc}, img={Img}, verdict={V})",
            scenario.ScenarioId, imageId, opinion.Verdict);
    }

    private static IEnumerable<string> RecentDates(int lookbackDays)
    {
        var today = DateTime.UtcNow.Date;
        for (var i = 0; i < Math.Max(1, lookbackDays); i++)
            yield return today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
