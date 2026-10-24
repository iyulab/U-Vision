using Microsoft.Extensions.Hosting;
using UVision.Api.Configuration;

namespace UVision.Api.Services.Memory;

/// <summary>
/// 기억 동기화 타이머 — 기억 단이 켜져 있으면 <see cref="MemoryOptions.SyncIntervalSeconds"/> 마다
/// <see cref="CaseMemory.SyncOnceAsync"/>. 꺼져 있으면(none 기본) 즉시 종료한다(부수효과 0). 실패는 다음 주기에 다시 한다.
/// </summary>
public sealed class CaseMemorySyncService(CaseMemory memory, MemoryOptions options, ILogger<CaseMemorySyncService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!memory.IsEnabled)
            return;

        var delay = TimeSpan.FromSeconds(Math.Max(5, options.SyncIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var added = await memory.SyncOnceAsync(stoppingToken);
                if (added > 0) log.LogInformation("기억 동기화 — 확정 사례 {Count}건 임베딩", added);
            }
            // 종료 신호만 루프를 끝낸다 — HTTP 시간 초과도 OperationCanceledException 이라 그것까지 끝내면 동기화가 영영 멈춘다.
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "기억 동기화 실패 — 다음 주기 재시도"); }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
