using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;
using UVision.Api.Services.Label;
using UVision.Api.Storage;

namespace UVision.Api.Services.Memory;

/// <summary>
/// 기억 단이 한 판정에 낸 것 — 판정기가 답했거나 기권했으면 <see cref="Answer"/>, 그 전에 실패했으면(임베딩 실패 · 기억이
/// 아직 그 인코더 판으로 지어지지 않음) <see cref="Failure"/>.
/// </summary>
/// <param name="EncoderKey">질의 이미지를 임베딩한 인코더 판(임베딩이 실패했으면 null).</param>
/// <param name="Scores">패치 모드를 켰을 때의 이상 점수(Shadow — 판정에 쓰이지 않는다). 점수를 못 냈으면 null.</param>
public sealed record MemoryOutcome(string? EncoderKey, MemoryAnswer? Answer, string? Failure, AnomalyScores? Scores = null)
{
    /// <summary>기권 사유 — 답했으면 null.</summary>
    public string? AbstainReason => Answer is { Class: not null } ? null : Failure ?? Answer?.Abstained;

    /// <summary>장부 annotation — 판정기의 거리 · 이웃 수와 인코더 판.</summary>
    public IReadOnlyDictionary<string, string> ToAnnotations()
    {
        var a = Answer is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(Answer.ToAnnotations(), StringComparer.Ordinal);
        if (EncoderKey is not null)
            a["memory:encoder"] = EncoderKey;
        if (Scores is not null)
            foreach (var (k, v) in Scores.ToAnnotations())
                a[k] = v;
        return a;
    }
}

/// <summary>
/// 이상 점수 한 쌍 — 같은 정상 기억에 견준 전역 점수(최근접 사진까지 코사인 거리)와 패치 점수(질의 패치마다 기억 패치 전부까지
/// 최근접 거리의 최댓값). 클수록 정상 기억과 멀다. 둘이 같은 기억 위에서 나와야 시나리오마다 재생으로 비교할 수 있다.
/// </summary>
/// <param name="BankSize">점수를 낸 정상 기억의 사진 수 — 기억 크기에 따라 두 점수의 우열이 갈려 재생이 이 값으로 나눠 본다.</param>
/// <param name="Map">패치별 거리 지도(열지도 근거) — 격자 모양을 알 수 없으면 null.</param>
public sealed record AnomalyScores(
    double Global, double Patch, int BankSize, AnomalyMap? Map = null, int? BankPatches = null, string? PatchEncoder = null)
{
    public IReadOnlyDictionary<string, string> ToAnnotations()
    {
        var a = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory:global_score"] = Global.ToString("0.######", CultureInfo.InvariantCulture),
            ["memory:patch_score"] = Patch.ToString("0.######", CultureInfo.InvariantCulture),
            ["memory:bank_size"] = BankSize.ToString(CultureInfo.InvariantCulture),
        };
        // 패치 수 상한으로 줄였을 때만 남긴다 — 점수가 줄인 기억 위에서 나왔다는 것을 재생이 알아야 한다(안 줄였으면 키가 없다).
        if (BankPatches is { } patches)
            a["memory:bank_patches"] = patches.ToString(CultureInfo.InvariantCulture);
        // 시나리오가 패치 인코더를 따로 골랐을 때만 — 패치 점수가 전역(설치) 인코더와 다른 공간에서 났다는 표시.
        if (PatchEncoder is not null)
            a["memory:patch_encoder"] = PatchEncoder;
        return a;
    }
}

/// <summary>
/// 정상 사례의 전역 · 패치 벡터를 모은 기억 — 패치는 사례 순서대로 한 배열에 이어 붙는다. 패치 수 상한(<c>PatchBankPatches</c>)으로
/// 줄였으면 <see cref="Patches"/> 는 사례 순서가 아니라 고른 패치의 모음이고, <see cref="PatchesPerImage"/> 는 질의 한 장의 패치 수(모양
/// 검사용)로 남는다 — 그래서 점수 계산은 <see cref="PoolCount"/> 를 쓴다. 패치는 설치 인코더가 아니라 시나리오가 고른 패치 인코더의
/// 공간일 수 있어(<see cref="PatchEncoderKey"/> · <see cref="PatchDimension"/>) 전역 벡터(<see cref="Globals"/>)와 판 · 차원이 다를 수 있다.
/// </summary>
internal sealed record PatchBank(
    IReadOnlyList<string> ImageIds, IReadOnlyList<float[]> Globals, float[] Patches, int PatchesPerImage, int PatchDimension,
    string PatchEncoderKey)
{
    public int Count => ImageIds.Count;

    /// <summary>점수에 쓰는 기억 패치의 수.</summary>
    public int PoolCount => Patches.Length / PatchDimension;

    /// <summary>패치 수 상한으로 줄었는가 — 사례 전부의 패치보다 적다.</summary>
    public bool Reduced => PoolCount < Count * PatchesPerImage;
}

/// <summary>
/// 확정 사례 기억 — 사람이 확정한 라벨 이미지의 임베딩으로 질의 이미지의 클래스를 답하거나 기권한다(system 1).
/// 이미지 내용만으로 클래스가 정해지는 검사에 선다.
/// <para>
/// 질의(<see cref="RecallAsync"/>)는 이미지 한 장을 임베딩해 메모리에 든 스냅숏과 비교만 한다 — 디스크를 읽지 않는다.
/// 스냅숏은 <see cref="SyncOnceAsync"/>(배경 서비스가 주기적으로 부름)가 짓는다: 확정 사례 가운데 아직 벡터가 없는 것을
/// 64장씩 임베딩해 벡터 캐시에 덧붙이고, 현재 라벨과 이어 스냅숏을 바꾼다. 그래서 라벨 변경은 다음 동기화부터 보인다.
/// </para>
/// <para>
/// 인코더 판이 바뀌면(질의 응답의 판 ≠ 스냅숏의 판) 그 질의는 기권하고 동기화가 새 판으로 다시 짓는다 — 판이 섞인
/// 이웃 비교는 없다. 모든 실패는 기권으로 흡수한다(degrade) — 판정을 막지 않는다.
/// </para>
/// </summary>
public sealed class CaseMemory(
    IImageEmbedder embedder,
    IMemoryVectorStore vectors,
    IPatchBankStore patchBank,
    IScenarioStore scenarios,
    IInspectionStore inspections,
    ILabelStore labels,
    StoragePaths paths,
    MemoryOptions options,
    ILogger<CaseMemory> logger)
{
    private sealed record Snapshot(
        string EncoderKey, IReadOnlyList<MemoryCase> Cases, DateTimeOffset BuiltAt, PatchBank? Bank = null);

    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);

    // 시나리오마다 가장 최근에 본 (전역) 인코더 판 — 질의 · 동기화 응답에서 배운다. 동기화가 이 판으로 그 시나리오의 기억을 짓는다.
    // 전역 기억은 설치 인코더 하나지만 시나리오마다 상태(질의에서 배운 판)를 따로 둔다.
    private readonly ConcurrentDictionary<string, string> _encoderKeys = new(StringComparer.Ordinal);

    // 시나리오가 패치 인코더를 따로 골랐을 때 그 인코더의 판 · 차원 — 응답(질의 · 동기화의 probe)에서 배운다. 안 골랐으면 항목이 없다.
    private readonly ConcurrentDictionary<string, PatchSpace> _patchSpaces = new(StringComparer.Ordinal);

    private sealed record PatchSpace(string Key, int Dimension);

    /// <summary>기억 단이 켜져 있는가(임베더가 있음).</summary>
    public bool IsEnabled => embedder.IsEnabled;

    // 패치 점수를 쌓는가 — 설정이 켜졌고 임베더가 패치 모드를 낸다(팩토리가 기동에서 이 조합을 보장한다).
    private bool PatchesActive => options.Patches && embedder.SupportsPatches;

    /// <summary>시나리오 기억의 지금 상태 — 지어진 판 · 사례 수 · 지은 시각. 아직 안 지어졌으면 null.</summary>
    public (string EncoderKey, int Cases, DateTimeOffset BuiltAt)? StatusOf(string scenarioId) =>
        _snapshots.TryGetValue(scenarioId, out var s) ? (s.EncoderKey, s.Cases.Count, s.BuiltAt) : null;

    /// <summary>질의 이미지 한 장의 답 또는 기권. 예외를 던지지 않는다.</summary>
    public async Task<MemoryOutcome> RecallAsync(
        EffectiveScenario scenario, ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        ImageEmbeddings embedded;
        ImageFeatures? features = null;
        PatchGrid? grid = null;
        PatchSpace? patchSpace = null;
        var clock = Stopwatch.StartNew();
        try
        {
            if (PatchesActive && scenario.PatchEncoder is null)
            {
                // 설치 인코더 하나 — 한 호출이 전역과 패치를 함께 준다. 전역은 일반 호출과 같은 값이라 판정기는 그대로 쓴다.
                var withPatches = await embedder.EmbedPatchesAsync([image], encoder: null, cancellationToken);
                features = withPatches.Images[0];
                grid = withPatches.Grid;
                embedded = new ImageEmbeddings(withPatches.Encoder, [features.Global]);
                patchSpace = new PatchSpace(withPatches.Encoder.Key, withPatches.Encoder.Dimension);
                _patchSpaces.TryRemove(scenario.ScenarioId, out _);
            }
            else
            {
                // 전역 기억은 언제나 설치 인코더 — 판정기의 임베딩이다. 시나리오가 패치 인코더를 따로 골랐으면 패치 특징은 그
                // 인코더로 병렬로 낸다(전역 기억의 거리 척도는 그대로, 패치 점수만 고른 층의 공간에서).
                // 로컬 async 함수로 감싼다 — 임베더 구현이 동기적으로 던져도 그 실패가 Task 안에 들어가, 패치 쪽 실패가 전역 기억의 기권으로 번지지 않는다.
                async Task<PatchEmbeddings> PatchesAsync() => await embedder.EmbedPatchesAsync([image], scenario.PatchEncoder, cancellationToken);
                var patchTask = PatchesActive ? PatchesAsync() : null;
                embedded = await embedder.EmbedAsync([image], cancellationToken);
                if (patchTask is not null)
                {
                    try
                    {
                        var withPatches = await patchTask;
                        features = withPatches.Images[0];
                        grid = withPatches.Grid;
                        patchSpace = new PatchSpace(withPatches.Encoder.Key, withPatches.Encoder.Dimension);
                        _patchSpaces[scenario.ScenarioId] = patchSpace;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // 패치 쪽 실패는 점수만 누락 — 전역 기억의 답은 그대로 낸다(degrade).
                        logger.LogWarning(ex, "패치 임베딩 실패 — 점수만 누락 (scenario={ScenarioId})", scenario.ScenarioId);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "기억 단 임베딩 실패 — 기권 (scenario={ScenarioId})", scenario.ScenarioId);
            return new(null, null, "embed unavailable: " + ex.Message);
        }

        var embedMs = clock.ElapsedMilliseconds;
        var key = embedded.Encoder.Key;
        _encoderKeys[scenario.ScenarioId] = key;
        if (!_snapshots.TryGetValue(scenario.ScenarioId, out var snapshot))
            return new(key, null, "memory not built yet");
        if (snapshot.EncoderKey != key)
            return new(key, null, $"memory built for {snapshot.EncoderKey}, query encoded by {key}");

        try
        {
            var answer = MemoryJudge.Decide(embedded.Vectors[0], snapshot.Cases, options.Neighbors, options.MaxDistance);
            var decideMs = clock.ElapsedMilliseconds - embedMs;
            var scores = features is not null && patchSpace is not null && snapshot.Bank is { } bank
                ? await ScoreAsync(scenario.ScenarioId, key, patchSpace.Key, bank, embedded.Vectors[0], features, grid, cancellationToken)
                : null;
            // 켠 비용이 어디서 드는지 — 운영에서 «켜기 전에 그 장비에서 잰다»(configuration.md) 의 눈금.
            logger.LogDebug(
                "기억 질의 {ScenarioId}: 임베딩 {EmbedMs} ms · 판정 {DecideMs} ms · 점수 {ScoreMs} ms (패치 기억 {Bank}장)",
                scenario.ScenarioId, embedMs, decideMs, clock.ElapsedMilliseconds - embedMs - decideMs, snapshot.Bank?.Count ?? 0);
            return new(key, answer, null, scores);
        }
        catch (ArgumentException ex)
        {
            // 같은 판인데 차원이 다른 벡터 — 캐시가 손상됐다. 기권하고 다음 동기화를 기다린다.
            logger.LogWarning(ex, "기억 단 비교 실패 — 기권 (scenario={ScenarioId})", scenario.ScenarioId);
            return new(key, null, "memory unreadable: " + ex.Message);
        }
    }

    // 이상 점수 — 판정에 쓰이지 않으니 계산이 틀려도 점수만 빠진다(degrade). CPU 일이라 스레드 풀로 보낸다.
    private async Task<AnomalyScores?> ScoreAsync(
        string scenarioId, string encoderKey, string patchKey, PatchBank bank, float[] queryGlobal, ImageFeatures features,
        PatchGrid? grid, CancellationToken cancellationToken)
    {
        if (bank.PatchEncoderKey != patchKey)
        {
            // 패치 기억이 다른 인코더 판으로 지어져 있다(시나리오의 패치 인코더가 바뀜) — 다음 동기화가 새 판으로 다시 짓는다.
            logger.LogDebug(
                "패치 점수 건너뜀 — 패치 기억 {Bank}, 질의 {Query} (scenario={ScenarioId})", bank.PatchEncoderKey, patchKey, scenarioId);
            return null;
        }
        if (queryGlobal.Length != bank.Globals[0].Length
            || features.Patches.Length != bank.PatchesPerImage * bank.PatchDimension)
        {
            logger.LogWarning(
                "패치 점수 건너뜀 — 질의 특징의 모양이 기억과 다르다 (scenario={ScenarioId})", scenarioId);
            return null;
        }
        try
        {
            return await Task.Run(() =>
            {
                var distances = PatchDistance.PatchMap(
                    features.Patches, bank.PatchesPerImage, bank.Patches, bank.PoolCount, bank.PatchDimension);
                var global = PatchDistance.GlobalScore(queryGlobal, bank.Globals);
                var patch = distances.Max();
                // 격자 모양을 알 때만 지도로 남긴다 — 어긋나면 점수만(지도를 그릴 수 없는 값은 만들지 않는다).
                var map = grid is { } g && g.Count == distances.Length
                    ? new AnomalyMap
                    {
                        Encoder = patchKey,
                        Rows = g.Rows,
                        Columns = g.Columns,
                        Crop = features.Crop is { } c ? new AnomalyCrop { X0 = c.X0, Y0 = c.Y0, X1 = c.X1, Y1 = c.Y1 } : null,
                        Distances = [.. distances.Select(d => Math.Round(d, 4))],
                        PatchScore = patch,
                        GlobalScore = global,
                        BankSize = bank.Count,
                    }
                    : null;
                return new AnomalyScores(
                    global, patch, bank.Count, map, bank.Reduced ? bank.PoolCount : null, patchKey != encoderKey ? patchKey : null);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "패치 점수 계산 실패 — 점수만 누락 (scenario={ScenarioId})", scenarioId);
            return null;
        }
    }

    /// <summary>
    /// 모든 검사 시나리오의 기억을 한 번 맞춘다 — 빠진 벡터를 채우고 스냅숏을 바꾼다. 시나리오 하나의 실패는 그 시나리오만
    /// 건너뛴다. 채운 벡터 수를 돌려준다.
    /// </summary>
    public async Task<int> SyncOnceAsync(CancellationToken cancellationToken)
    {
        if (!embedder.IsEnabled)
            return 0;

        var added = 0;
        foreach (var scenarioId in paths.ScenarioIds())
        {
            try
            {
                if (await scenarios.GetAsync(scenarioId, cancellationToken) is not { } scenario)
                    continue;
                var effective = EffectiveScenario.Resolve(scenario, mlEnabled: false, memoryEnabled: true);
                if (!effective.Tiers.Contains(ScenarioVocabulary.MemoryTier))
                {
                    _snapshots.TryRemove(scenarioId, out _);
                    continue;
                }
                added += await SyncScenarioAsync(effective, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "기억 동기화 실패 — 다음 주기 재시도 (scenario={ScenarioId})", scenarioId);
            }
        }
        return added;
    }

    private async Task<int> SyncScenarioAsync(EffectiveScenario scenario, CancellationToken cancellationToken)
    {
        // 확정 사례 = 사람이 확정했고 감사 충돌이 없고 시나리오가 선언한 클래스인 라벨. 라벨 사이드카만 읽는다 — 이 일은
        // 주기마다 돌므로 판정 레코드 전부를 읽는 학습 후보 조인을 쓰지 않는다. 짝 이미지가 없는 라벨은 아래에서 건너뛴다.
        var confirmed = new List<ConfirmedCase>();
        foreach (var date in await inspections.ListDatesAsync(scenario.ScenarioId, cancellationToken))
        {
            foreach (var label in await labels.ListAsync(scenario.ScenarioId, date, cancellationToken))
            {
                if (label.OperativeLabel is { } operative
                    && label.Audit?.Status != LabelAuditStatus.Conflicted
                    && scenario.ClassFor(operative) is { } cls)
                    confirmed.Add(new ConfirmedCase(label.ImageId, date, cls));
            }
        }

        var key = _encoderKeys.GetValueOrDefault(scenario.ScenarioId);
        var cached = key is null
            ? new Dictionary<string, float[]>(StringComparer.Ordinal)
            : new Dictionary<string, float[]>(await vectors.ReadAsync(scenario.ScenarioId, key, cancellationToken), StringComparer.Ordinal);
        var added = 0;

        foreach (var batch in confirmed.Where(c => !cached.ContainsKey(c.ImageId)).Chunk(IImageEmbedder.MaxBatch))
        {
            var images = new List<(ConfirmedCase Case, ReadOnlyMemory<byte> Data)>(batch.Length);
            foreach (var c in batch)
            {
                if (await inspections.ReadImageAsync(scenario.ScenarioId, c.Date, c.ImageId, cancellationToken) is { } image)
                    images.Add((c, image.Data));
            }
            if (images.Count == 0)
                continue;

            var embedded = await embedder.EmbedAsync([.. images.Select(i => i.Data)], cancellationToken);
            var batchKey = embedded.Encoder.Key;
            if (key is null)
            {
                // 처음 배운 판 — 그 판의 캐시를 읽어 이미 있는 것은 다시 적지 않는다.
                key = batchKey;
                _encoderKeys[scenario.ScenarioId] = key;
                foreach (var (id, v) in await vectors.ReadAsync(scenario.ScenarioId, key, cancellationToken))
                    cached.TryAdd(id, v);
            }
            else if (batchKey != key)
            {
                // 인코더가 바뀌었다 — 이번 판의 벡터는 버리고 다음 주기에 새 판으로 처음부터 짓는다.
                _encoderKeys[scenario.ScenarioId] = batchKey;
                logger.LogInformation(
                    "기억 인코더 판 변경 {Old} → {New} — 다음 동기화에서 다시 짓는다 (scenario={ScenarioId})",
                    key, batchKey, scenario.ScenarioId);
                return added;
            }

            var fresh = images
                .Zip(embedded.Vectors, (i, v) => new MemoryVector(i.Case.ImageId, v))
                .Where(v => !cached.ContainsKey(v.ImageId))
                .ToList();
            await vectors.AppendAsync(scenario.ScenarioId, key, fresh, cancellationToken);
            foreach (var v in fresh)
                cached[v.ImageId] = v.Vector;
            added += fresh.Count;
        }

        if (key is null)
        {
            // 확정 사례가 없거나 이미지를 하나도 못 읽었다 — 판을 모르니 지을 것이 없다.
            _snapshots.TryRemove(scenario.ScenarioId, out _);
            return added;
        }

        var cases = confirmed
            .Where(c => cached.ContainsKey(c.ImageId))
            .Select(c => new MemoryCase(c.ImageId, c.Class, cached[c.ImageId]))
            .ToList();
        PatchBank? bank = null;
        if (PatchesActive)
        {
            _snapshots.TryGetValue(scenario.ScenarioId, out var previous);
            bank = await BuildPatchBankAsync(
                scenario, confirmed, cached, key, previous?.EncoderKey == key ? previous.Bank : null, cancellationToken);
        }
        _snapshots[scenario.ScenarioId] = new Snapshot(key, cases, DateTimeOffset.UtcNow, bank);
        return added;
    }

    /// <summary>
    /// 정상 확정 사례(가장 최근 <see cref="MemoryOptions.PatchBankCap"/> 장)의 패치 기억을 짓는다 — 디스크 캐시에 없는 것만
    /// 패치 모드로 임베딩해 쓴다. 같은 사례 집합이면 지난 기억을 그대로 쓴다(주기마다 수십 MB 를 다시 읽지 않는다). 실패는
    /// 지난 기억을 유지한다 — 점수만 낡을 뿐 판정은 영향이 없다.
    /// </summary>
    private async Task<PatchBank?> BuildPatchBankAsync(
        EffectiveScenario scenario, List<ConfirmedCase> confirmed, Dictionary<string, float[]> globals, string key,
        PatchBank? previous, CancellationToken cancellationToken)
    {
        try
        {
            var normal = confirmed
                .Where(c => string.Equals(c.Class, ScenarioVocabulary.Ok, StringComparison.OrdinalIgnoreCase)
                            && globals.ContainsKey(c.ImageId))
                .OrderByDescending(c => c.Date, StringComparer.Ordinal)
                .ThenByDescending(c => c.ImageId, StringComparer.Ordinal)
                .Take(options.PatchBankCap)
                .ToList();
            if (normal.Count == 0)
                return null;

            // 패치의 인코더 공간 — 안 골랐으면 설치 인코더(전역과 같은 판 · 차원), 골랐으면 그 인코더의 판 · 차원(응답에서 배운다).
            var learned = scenario.PatchEncoder is null ? null : _patchSpaces.GetValueOrDefault(scenario.ScenarioId);
            var knownKey = scenario.PatchEncoder is null ? key : learned?.Key;
            if (previous is not null && knownKey is not null && previous.PatchEncoderKey == knownKey
                && previous.ImageIds.SequenceEqual(normal.Select(c => c.ImageId)))
                return previous;
            var space = scenario.PatchEncoder is null
                ? new PatchSpace(key, globals[normal[0].ImageId].Length)
                : learned ?? await ProbePatchSpaceAsync(scenario, normal[0], cancellationToken);

            var patches = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var c in normal)
            {
                if (await patchBank.ReadAsync(scenario.ScenarioId, space.Key, c.ImageId, cancellationToken: cancellationToken) is { } stored
                    && stored.Length % space.Dimension == 0)
                    patches[c.ImageId] = stored;
            }

            foreach (var batch in normal.Where(c => !patches.ContainsKey(c.ImageId)).Chunk(IImageEmbedder.MaxPatchBatch))
            {
                var images = new List<(ConfirmedCase Case, ReadOnlyMemory<byte> Data)>(batch.Length);
                foreach (var c in batch)
                {
                    if (await inspections.ReadImageAsync(scenario.ScenarioId, c.Date, c.ImageId, cancellationToken) is { } image)
                        images.Add((c, image.Data));
                }
                if (images.Count == 0)
                    continue;
                var embedded = await embedder.EmbedPatchesAsync([.. images.Select(i => i.Data)], scenario.PatchEncoder, cancellationToken);
                if (embedded.Encoder.Key != space.Key)
                {
                    // 인코더가 바뀌었다 — 이 판의 패치는 못 쓴다. 다음 동기화가 새 판으로 짓는다.
                    logger.LogInformation(
                        "패치 기억 중 인코더 판 변경 {Old} → {New} (scenario={ScenarioId})", space.Key, embedded.Encoder.Key, scenario.ScenarioId);
                    if (scenario.PatchEncoder is not null)
                        _patchSpaces[scenario.ScenarioId] = new PatchSpace(embedded.Encoder.Key, embedded.Encoder.Dimension);
                    return previous;
                }
                for (var i = 0; i < images.Count; i++)
                {
                    var vector = embedded.Images[i].Patches;
                    await patchBank.WriteAsync(scenario.ScenarioId, space.Key, images[i].Case.ImageId, vector, cancellationToken);
                    patches[images[i].Case.ImageId] = vector;
                }
            }

            // 모두 같은 패치 수여야 한 배열로 이어 붙는다 — 첫 사례와 다른 모양(손상 · 다른 격자)은 뺀다.
            var ready = normal.Where(c => patches.ContainsKey(c.ImageId)).ToList();
            if (ready.Count == 0)
                return null;
            var length = patches[ready[0].ImageId].Length;
            ready = [.. ready.Where(c => patches[c.ImageId].Length == length)];
            var flat = new float[ready.Count * length];
            for (var i = 0; i < ready.Count; i++)
                patches[ready[i].ImageId].CopyTo(flat, i * length);
            // 패치 수 상한 — 모은 패치가 상한보다 많으면 k-center 로 줄인다(CPU 일이라 스레드 풀로, 요청 경로 밖인 동기화에서).
            if (options.PatchBankPatches > 0 && flat.Length / space.Dimension > options.PatchBankPatches)
            {
                var pool = flat.Length / space.Dimension;
                var started = Stopwatch.StartNew();
                flat = await Task.Run(() => ReducePatches(flat, pool, space.Dimension, options.PatchBankPatches), cancellationToken);
                logger.LogInformation(
                    "패치 기억을 {From} → {To} 패치로 줄임 — k-center {Ms} ms (scenario={ScenarioId})",
                    pool, options.PatchBankPatches, started.ElapsedMilliseconds, scenario.ScenarioId);
            }
            // 기억 밖으로 밀려난 사례의 파일을 치운다 — 정상이 새로 확정될 때마다 가장 오래된 것이 빠진다.
            await patchBank.PruneAsync(scenario.ScenarioId, space.Key, normal.Select(c => c.ImageId).ToHashSet(StringComparer.Ordinal), cancellationToken);
            return new PatchBank(
                [.. ready.Select(c => c.ImageId)], [.. ready.Select(c => globals[c.ImageId])], flat, length / space.Dimension,
                space.Dimension, space.Key);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "패치 기억 동기화 실패 — 지난 기억 유지 (scenario={ScenarioId})", scenario.ScenarioId);
            return previous;
        }
    }

    // 패치 인코더의 판 · 차원은 응답에만 있다 — 캐시를 읽기 전에 정상 한 장을 패치 모드로 임베딩해 배운다(MLoop 에 없는 인코더 id 면
    // 여기서 예외 → 호출한 쪽이 «지난 기억 유지» 로 흡수하고 점수만 누락된다).
    private async Task<PatchSpace> ProbePatchSpaceAsync(EffectiveScenario scenario, ConfirmedCase sample, CancellationToken cancellationToken)
    {
        var image = await inspections.ReadImageAsync(scenario.ScenarioId, sample.Date, sample.ImageId, cancellationToken)
            ?? throw new InvalidOperationException("패치 인코더를 알아볼 정상 사례 이미지를 읽지 못했다");
        var probe = await embedder.EmbedPatchesAsync([image.Data], scenario.PatchEncoder, cancellationToken);
        var space = new PatchSpace(probe.Encoder.Key, probe.Encoder.Dimension);
        _patchSpaces[scenario.ScenarioId] = space;
        return space;
    }

    /// <summary>모은 패치 <paramref name="count"/> 개 가운데 <paramref name="keep"/> 개를 k-center 로 골라 새 배열로 모은다.</summary>
    internal static float[] ReducePatches(float[] vectors, int count, int dimension, int keep)
    {
        var chosen = CoresetSelector.Select(vectors, count, dimension, keep);
        var reduced = new float[chosen.Length * dimension];
        for (var i = 0; i < chosen.Length; i++)
            Array.Copy(vectors, (long)chosen[i] * dimension, reduced, (long)i * dimension, dimension);
        return reduced;
    }

    private sealed record ConfirmedCase(string ImageId, string Date, string Class);
}
