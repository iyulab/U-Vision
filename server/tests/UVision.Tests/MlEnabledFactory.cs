using Microsoft.AspNetCore.Hosting;
using UVision.Api;

namespace UVision.Tests;

/// <summary>
/// ML 분류기를 <c>mock</c> 으로 활성화한 테스트 팩토리 — ③ 2중체크 wire 경로 검증용.
/// 기본 <see cref="UVisionApiFactory"/> 는 ML none(VLM 단독)이라 additive 필드가 생략된다.
/// </summary>
public sealed class MlEnabledFactory : UVisionApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting($"{UVisionOptions.SectionName}:Ml:Provider", "mock");
    }
}
