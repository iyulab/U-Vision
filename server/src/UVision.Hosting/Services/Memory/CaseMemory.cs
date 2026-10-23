using System.Collections.Concurrent;
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
public sealed record MemoryOutcome(string? EncoderKey, MemoryAnswer? Answer, string? Failure)
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
        return a;
    }
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
    IScenarioStore scenarios,
    IInspectionStore inspections,
    ILabelStore labels,
    StoragePaths paths,
    MemoryOptions options,
    ILogger<CaseMemory> logger)
{
    private sealed record Snapshot(string EncoderKey, IReadOnlyList<MemoryCase> Cases, DateTimeOffset BuiltAt);

    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);

    // 가장 최근에 본 인코더 판 — 질의 · 동기화 응답에서 배운다. 동기화가 이 판으로 기억을 짓는다.
    private volatile string? _encoderKey;

    /// <summary>기억 단이 켜져 있는가(임베더가 있음).</summary>
    public bool IsEnabled => embedder.IsEnabled;

    /// <summary>시나리오 기억의 지금 상태 — 지어진 판 · 사례 수 · 지은 시각. 아직 안 지어졌으면 null.</summary>
    public (string EncoderKey, int Cases, DateTimeOffset BuiltAt)? StatusOf(string scenarioId) =>
        _snapshots.TryGetValue(scenarioId, out var s) ? (s.EncoderKey, s.Cases.Count, s.BuiltAt) : null;

    /// <summary>질의 이미지 한 장의 답 또는 기권. 예외를 던지지 않는다.</summary>
    public async Task<MemoryOutcome> RecallAsync(
        EffectiveScenario scenario, ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        ImageEmbeddings embedded;
        try
        {
            embedded = await embedder.EmbedAsync([image], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "기억 단 임베딩 실패 — 기권 (scenario={ScenarioId})", scenario.ScenarioId);
            return new(null, null, "embed unavailable: " + ex.Message);
        }

        var key = embedded.Encoder.Key;
        _encoderKey = key;
        if (!_snapshots.TryGetValue(scenario.ScenarioId, out var snapshot))
            return new(key, null, "memory not built yet");
        if (snapshot.EncoderKey != key)
            return new(key, null, $"memory built for {snapshot.EncoderKey}, query encoded by {key}");

        try
        {
            var answer = MemoryJudge.Decide(embedded.Vectors[0], snapshot.Cases, options.Neighbors, options.MaxDistance);
            return new(key, answer, null);
        }
        catch (ArgumentException ex)
        {
            // 같은 판인데 차원이 다른 벡터 — 캐시가 손상됐다. 기권하고 다음 동기화를 기다린다.
            logger.LogWarning(ex, "기억 단 비교 실패 — 기권 (scenario={ScenarioId})", scenario.ScenarioId);
            return new(key, null, "memory unreadable: " + ex.Message);
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

        var key = _encoderKey;
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
                _encoderKey = key;
                foreach (var (id, v) in await vectors.ReadAsync(scenario.ScenarioId, key, cancellationToken))
                    cached.TryAdd(id, v);
            }
            else if (batchKey != key)
            {
                // 인코더가 바뀌었다 — 이번 판의 벡터는 버리고 다음 주기에 새 판으로 처음부터 짓는다.
                _encoderKey = batchKey;
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
        _snapshots[scenario.ScenarioId] = new Snapshot(key, cases, DateTimeOffset.UtcNow);
        return added;
    }

    private sealed record ConfirmedCase(string ImageId, string Date, string Class);
}
