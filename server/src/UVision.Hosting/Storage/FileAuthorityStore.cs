using System.Globalization;
using System.Text.Json;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// 파일시스템 권한 단계 저장소 — <c>{scenario}/authority.json</c>(atomic 교체 + append-only history).
/// 경로 지식은 <see cref="StoragePaths"/> 가 소유. read→write 원자성은 <see cref="SemaphoreSlim"/> 직렬화
/// (단일 호스트 단일 프로세스 전제 — B1 <see cref="FileModelRegistry"/> 와 동일).
/// </summary>
public sealed class FileAuthorityStore : IAuthorityStore
{
    private readonly StoragePaths _paths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileAuthorityStore(StoragePaths paths) => _paths = paths;

    public async Task<AuthorityState?> ReadAsync(string scenarioId, CancellationToken ct = default)
    {
        var path = _paths.AuthorityFile(scenarioId); // scenarioId 검증(ArgumentException→400)
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AuthorityState>(stream, StoragePaths.Json, ct);
    }

    public async Task SetStageAsync(
        string scenarioId, AuthorityStage stage, string by, string mode, string? reason,
        CancellationToken ct = default)
    {
        StoragePaths.Id(scenarioId); // 화이트리스트 거부(read 전에도 검증)
        await _writeLock.WaitAsync(ct);
        try
        {
            var current = await ReadAsync(scenarioId, ct);
            var from = current?.Stage ?? AuthorityStage.Advisory; // 부재 baseline = advisory
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            var transition = new AuthorityTransition
            {
                From = AuthorityStageJsonConverter.ToWire(from),
                To = AuthorityStageJsonConverter.ToWire(stage),
                At = now, By = by, Mode = mode, Reason = reason,
            };
            var history = new List<AuthorityTransition>(current?.History ?? []) { transition };

            var state = new AuthorityState
            {
                Stage = stage,
                PreviousStage = from,
                UpdatedAt = now,
                UpdatedBy = by,
                Reason = reason,
                History = history,
            };
            await StoragePaths.AtomicWriteJsonAsync(_paths.AuthorityFile(scenarioId), state, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
