using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Services.Dataset;
using UVision.Api.Storage;

namespace UVision.Api.Services.Retention;

/// <summary>삭제할 수 있는 정본 부류의 이름 — 단일 출처. 이름에 없는 것은 지우지 않는다.</summary>
public static class RetentionKinds
{
    public const string Records = "records";
    public const string Ledger = "ledger";
    public const string Metrics = "metrics";

    public static readonly IReadOnlyList<string> All = [Records, Ledger, Metrics];
}

/// <summary>계획과 삭제 사이에 구간 안이 달라졌거나 지문이 없다 — 호출자는 계획을 다시 받아야 한다(HTTP 409).</summary>
public sealed class RetentionConflictException(string message) : Exception(message);

/// <summary>
/// 보존 삭제 — 설치자가 날짜 구간과 부류를 이름으로 골라 정본을 지운다. 엔진은 아무것도 스스로 지우지 않는다.
/// <list type="bullet">
/// <item>계획(<c>dry_run</c>, 기본)과 삭제는 <b>같은 계산</b>이다: 구간 안 날짜 폴더 · 장부 · 메트릭 파일을 열거해 크기 · 잃는 것 · 지문을 낸다.</item>
/// <item>삭제는 계획의 지문을 되받아야 하고, 지금 다시 계산한 지문과 다르면 거부한다(<see cref="RetentionConflictException"/>) —
/// 미리 본 것과 같은 것만 지운다. 서버 상태 없이(재시작에도) 성립한다.</item>
/// <item>오늘(UTC)과 그 뒤는 구간에 못 든다 — 진행 중인 판정의 쓰기와 겹치지 않는다.</item>
/// <item><c>records</c> 는 날짜 폴더 통째로(사진 · 레코드 · 라벨 · 이상 지도), <c>ledger</c> · <c>metrics</c> 는 날짜 파일 통째로. 한 건만 지우는 경로는 없다.</item>
/// <item>지울 때마다 <c>{scenario}/retention.jsonl</c> 에 한 줄 남긴다(끝내 마치지 못해도).</item>
/// </list>
/// 저장소 경로 지식은 <see cref="StoragePaths"/> 가 소유한다 — 이 서비스는 그 경로만 쓴다.
/// </summary>
public sealed class RetentionService
{
    /// <summary>구간 상한(일) — 한 요청이 열거할 날짜 수를 묶는다.</summary>
    public const int MaxRangeDays = 3660;

    private static readonly JsonSerializerOptions LineJson = new(StoragePaths.Json) { WriteIndented = false };

    private readonly StoragePaths _paths;
    private readonly ILabelStore _labels;
    private readonly IDatasetExporter _exporter;
    private readonly IQuestionStore _questions;
    private readonly ILogger<RetentionService> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1); // 삭제는 한 번에 하나 — 계획과 삭제가 같은 구간을 보도록

    public RetentionService(
        StoragePaths paths, ILabelStore labels, IDatasetExporter exporter, IQuestionStore questions,
        ILogger<RetentionService> logger, TimeProvider? clock = null)
    {
        _paths = paths;
        _labels = labels;
        _exporter = exporter;
        _questions = questions;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <exception cref="ArgumentException">식별자 · 날짜 · 구간 · 부류 위반(HTTP 400).</exception>
    /// <exception cref="RetentionConflictException">실제 삭제인데 지문이 없거나 지금 계획과 다르다(HTTP 409).</exception>
    public async Task<RetentionPurgeResponse> RunAsync(
        string scenarioId, RetentionPurgeInput input, CancellationToken ct = default)
    {
        StoragePaths.Id(scenarioId);
        var from = StoragePaths.Date(input.From);
        var to = StoragePaths.Date(input.To);
        var today = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (string.CompareOrdinal(from, to) > 0)
            throw new ArgumentException("from 이 to 보다 늦다");
        if (string.CompareOrdinal(to, today) >= 0)
            throw new ArgumentException($"to 는 오늘({today}, UTC)보다 앞서야 한다 — 진행 중인 날은 지우지 않는다");
        if (DateOnly.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber
            - DateOnly.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber >= MaxRangeDays)
            throw new ArgumentException($"구간은 {MaxRangeDays}일을 넘을 수 없다");
        var kinds = NormalizeKinds(input.Kinds);
        var dryRun = input.DryRun ?? true;
        if (!dryRun && string.IsNullOrWhiteSpace(input.PlanFingerprint))
            throw new RetentionConflictException("실제 삭제는 바로 직전 계획(dry_run)의 plan_fingerprint 가 필요하다");

        await _gate.WaitAsync(ct);
        try
        {
            var plan = await PlanAsync(scenarioId, from, to, kinds, ct);
            if (dryRun)
                return plan.ToResponse(dryRun: true, purged: false);

            if (!string.Equals(plan.Fingerprint, input.PlanFingerprint, StringComparison.Ordinal))
                throw new RetentionConflictException("계획이 달라졌다(구간 안 파일이 바뀌었거나 다른 구간의 지문이다) — dry_run 을 다시 받아 확인한 뒤 지운다");

            await ExecuteAsync(scenarioId, plan, ct);
            return plan.ToResponse(dryRun: false, purged: true);
        }
        finally { _gate.Release(); }
    }

    private static IReadOnlyList<string> NormalizeKinds(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
            throw new ArgumentException("kinds 가 비었다 — 지울 부류를 이름으로 고른다(records · ledger · metrics)");
        foreach (var k in requested)
            if (!RetentionKinds.All.Contains(k, StringComparer.Ordinal))
                throw new ArgumentException($"알 수 없는 부류: '{k}' (records · ledger · metrics)");
        // 고정 순서로 — 같은 선택은 같은 지문을 낸다.
        return [.. RetentionKinds.All.Where(a => requested.Contains(a, StringComparer.Ordinal))];
    }

    private async Task<Plan> PlanAsync(
        string scenarioId, string from, string to, IReadOnlyList<string> kinds, CancellationToken ct)
    {
        bool InRange(string date) => string.CompareOrdinal(date, from) >= 0 && string.CompareOrdinal(date, to) <= 0;

        var fingerprintLines = new List<string>();
        var summaries = new List<RetentionKindSummary>();
        var recordDirs = new List<string>();
        var dayFiles = new List<string>();
        int results = 0, humanLabels = 0, notExported = 0, openQuestions = 0, decisions = 0, metricRows = 0;
        bool? covered = null;

        if (kinds.Contains(RetentionKinds.Records))
        {
            var exported = new Lazy<Task<HashSet<string>>>(() => ExportedKeysAsync(scenarioId, ct));
            int days = 0, files = 0;
            long bytes = 0;
            foreach (var (date, dir) in _paths.DateDirs(scenarioId).Where(d => InRange(d.Date)).OrderBy(d => d.Date, StringComparer.Ordinal))
            {
                days++;
                recordDirs.Add(dir);
                var info = new DirectoryInfo(dir);
                foreach (var f in info.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    files++;
                    bytes += f.Length;
                    fingerprintLines.Add($"records|{date}/{Path.GetRelativePath(dir, f.FullName).Replace('\\', '/')}|{f.Length}");
                }
                results += info.EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                    .Count(f => !f.Name.EndsWith(".label.json", StringComparison.Ordinal));
                foreach (var label in await _labels.ListAsync(scenarioId, date, ct))
                {
                    if (label.OperativeLabel is null) continue;
                    humanLabels++;
                    if (!(await exported.Value).Contains($"{date}/{label.ImageId}"))
                        notExported++;
                }
            }
            covered = notExported == 0;
            openQuestions = (await _questions.ListAsync(scenarioId, ct)).Count(q => q.State == QuestionState.Open && q.Source is null && InRange(q.Date));
            summaries.Add(new RetentionKindSummary { Kind = RetentionKinds.Records, Days = days, Files = files, Bytes = bytes });
        }

        if (kinds.Contains(RetentionKinds.Ledger))
        {
            var (s, rows) = SummarizeDatedFiles(RetentionKinds.Ledger, _paths.LedgerJsonl(scenarioId, "2000-01-01"), InRange, fingerprintLines, dayFiles);
            summaries.Add(s);
            decisions = rows;
        }

        if (kinds.Contains(RetentionKinds.Metrics))
        {
            var (s, rows) = SummarizeDatedFiles(RetentionKinds.Metrics, _paths.MetricsJsonl(scenarioId, "2000-01-01"), InRange, fingerprintLines, dayFiles);
            summaries.Add(s);
            metricRows = rows;
        }

        fingerprintLines.Sort(StringComparer.Ordinal);
        var header = $"{scenarioId}|{from}|{to}|{string.Join(',', kinds)}";
        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(header + "\n" + string.Join('\n', fingerprintLines))));

        return new Plan(scenarioId, from, to, kinds, fingerprint, summaries,
            new RetentionLoss
            {
                Results = results, HumanLabels = humanLabels, LabelsNotExported = notExported, OpenQuestions = openQuestions,
                Decisions = decisions, MetricRows = metricRows,
            },
            covered, recordDirs, dayFiles);
    }

    /// <summary>날짜가 이름인 jsonl(<c>{dir}/{date}.jsonl</c>)의 구간 안 파일을 센다. 행 수는 비지 않은 줄.</summary>
    private static (RetentionKindSummary Summary, int Rows) SummarizeDatedFiles(
        string kind, string anyDatedPath, Func<string, bool> inRange, List<string> fingerprintLines, List<string> collect)
    {
        var dir = Path.GetDirectoryName(anyDatedPath)!;
        int days = 0, rows = 0;
        long bytes = 0;
        if (Directory.Exists(dir))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
            {
                var date = Path.GetFileNameWithoutExtension(path);
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    || !inRange(date))
                    continue;
                var size = new FileInfo(path).Length;
                days++;
                bytes += size;
                rows += File.ReadLines(path).Count(l => !string.IsNullOrWhiteSpace(l));
                fingerprintLines.Add($"{kind}|{date}|{size}");
                collect.Add(path);
            }
        }
        return (new RetentionKindSummary { Kind = kind, Days = days, Files = days, Bytes = bytes }, rows);
    }

    /// <summary>모든 데이터셋 export 가 담은 <c>{date}/{image_id}</c> 키.</summary>
    private async Task<HashSet<string>> ExportedKeysAsync(string scenarioId, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exportId in await _exporter.ListExportsAsync(scenarioId, ct))
        {
            var manifest = await _exporter.ReadManifestAsync(scenarioId, exportId, ct);
            if (manifest is null) continue;
            foreach (var item in manifest.Items)
                keys.Add($"{item.Date}/{item.ImageId}");
        }
        return keys;
    }

    private async Task ExecuteAsync(string scenarioId, Plan plan, CancellationToken ct)
    {
        var completed = false;
        try
        {
            foreach (var dir in plan.RecordDirs)
                Directory.Delete(dir, recursive: true);
            foreach (var file in plan.DatedFiles)
                File.Delete(file);
            // 지운 사진을 가리키는 열린 문제는 풀 수 없다 — 그대로 두면 풀이자가 사진 없는 같은 문제를 다시 받는다.
            if (plan.Kinds.Contains(RetentionKinds.Records))
                foreach (var q in await _questions.ListAsync(scenarioId, ct))
                    if (q.State == QuestionState.Open && q.Source is null // 심은 문제는 결과 사진을 가리키지 않는다
                        && string.CompareOrdinal(q.Date, plan.From) >= 0 && string.CompareOrdinal(q.Date, plan.To) <= 0)
                        await _questions.TryCloseAsync(scenarioId, q.QuestionId, QuestionState.Withdrawn, null, "retention", ct);
            completed = true;
        }
        finally
        {
            // 마치지 못해도 한 줄은 남긴다 — «왜 이 날의 기록이 반쯤 없는가» 에 답하는 근거.
            await AppendRecordAsync(scenarioId, plan, completed, ct);
        }
        _logger.LogInformation(
            "보존 삭제 완료 (scenario={ScenarioId}, {From}~{To}, kinds={Kinds}, 파일 {Files}개)",
            scenarioId, plan.From, plan.To, string.Join(',', plan.Kinds), plan.Summaries.Sum(s => s.Files));
    }

    private async Task AppendRecordAsync(string scenarioId, Plan plan, bool completed, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(new
        {
            at = _clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture),
            from = plan.From,
            to = plan.To,
            kinds = plan.Kinds,
            completed,
            summary = plan.Summaries,
            lost = plan.Lost,
            plan_fingerprint = plan.Fingerprint,
        }, LineJson);
        var path = _paths.RetentionJsonl(scenarioId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, line + "\n", Encoding.UTF8, ct);
    }

    private sealed record Plan(
        string ScenarioId, string From, string To, IReadOnlyList<string> Kinds, string Fingerprint,
        IReadOnlyList<RetentionKindSummary> Summaries, RetentionLoss Lost, bool? Covered,
        IReadOnlyList<string> RecordDirs, IReadOnlyList<string> DatedFiles)
    {
        public RetentionPurgeResponse ToResponse(bool dryRun, bool purged) => new()
        {
            ScenarioId = ScenarioId, From = From, To = To, DryRun = dryRun, Purged = purged,
            PlanFingerprint = Fingerprint, Kinds = Summaries, Lost = Lost, CoveredByExport = Covered,
        };
    }
}
