using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UVision.Api.Configuration;

namespace UVision.Api.Services.Oracle;

/// <summary>
/// 오라클 스윕 타이머(④-B) — provider 활성 시 SweepIntervalSeconds 마다 SweepOnceAsync 호출.
/// provider 비활성(none 기본)이면 즉시 종료 → ④-B 이전과 동일(부수효과 0). 전체 실패도 degrade-safe.
/// </summary>
public sealed class OracleSweepBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOracleProvider _oracle;
    private readonly OracleOptions _options;
    private readonly ILogger<OracleSweepBackgroundService> _log;

    public OracleSweepBackgroundService(
        IServiceScopeFactory scopes, IOracleProvider oracle, OracleOptions options,
        ILogger<OracleSweepBackgroundService> log)
    { _scopes = scopes; _oracle = oracle; _options = options; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_oracle.IsEnabled)
            return; // none → 미동작

        var delay = TimeSpan.FromSeconds(Math.Max(5, _options.SweepIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<OracleSweepService>();
                var n = await svc.SweepOnceAsync(stoppingToken);
                if (n > 0) _log.LogInformation("오라클 스윕 — {Count}건 처리", n);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "오라클 스윕 실패 — 다음 주기 재시도"); }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
