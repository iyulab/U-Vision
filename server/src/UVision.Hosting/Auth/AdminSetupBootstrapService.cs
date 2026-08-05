using Microsoft.Extensions.Hosting;

namespace UVision.Api.Auth;

/// <summary>
/// 부팅 시 관리자 PIN 부트스트랩을 준비한다 — PIN 미설정이면 일회용 setup-token 을 생성하고 경로를
/// 로그에 남긴다(<see cref="AdminPinService.EnsureBootstrapAsync"/>). config PIN 설정 시엔 아무 것도
/// 하지 않으므로 기존 동작과 byte-identical.
/// </summary>
public sealed class AdminSetupBootstrapService : IHostedService
{
    private readonly AdminPinService _pinService;

    public AdminSetupBootstrapService(AdminPinService pinService) => _pinService = pinService;

    public Task StartAsync(CancellationToken cancellationToken) =>
        _pinService.EnsureBootstrapAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
