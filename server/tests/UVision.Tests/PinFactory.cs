using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UVision.Api.Auth;

namespace UVision.Tests;

/// <summary>ADMIN_PIN 이 설정된 테스트 팩토리 — 관리자 인증이 필요한 변경 경로 검증용.</summary>
public sealed class PinFactory : UVisionApiFactory
{
    public const string Pin = "1234";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton(new AdminPinOptions { Pin = Pin }));
    }
}
