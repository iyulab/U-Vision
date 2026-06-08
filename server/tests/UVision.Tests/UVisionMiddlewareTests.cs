using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UVision.Api;
using UVision.Api.Endpoints;
using Xunit;

namespace UVision.Tests;

public class UVisionMiddlewareTests
{
    private static IHost BuildHost() =>
        new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(s =>
            {
                s.AddLogging();
                s.AddRouting();
                var config = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["UVision:Vlm:Provider"] = "mock" }).Build();
                s.AddUVision(config, contentRoot: Path.GetTempPath());
            });
            web.Configure(app =>
            {
                app.UseUVision();
                app.UseRouting();
                app.UseEndpoints(e =>
                {
                    e.MapUVisionEndpoints("/api/u-vision");
                    // 호스트의 비-U-Vision 엔드포인트(camelCase 기본) — 오염 검증용.
                    e.MapGet("/host/probe", () => Results.Ok(new HostProbe("v")));
                    // basePath(/u-vision) prefix 를 공유하지만 별개인 호스트 경로 — 오삼킴 방지 검증용.
                    e.MapGet("/u-visionary", () => Results.Ok("host-route"));
                });
            });
        }).Build();

    private sealed record HostProbe(string HostValue);

    [Fact]
    public async Task Health_Endpoint_Responds_Under_ApiBasePath()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var res = await client.GetAsync("/api/u-vision/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task AddUVision_Does_Not_Pollute_Host_Json_Casing()
    {
        // AddUVision 은 전역 JsonOptions 를 건드리지 않는다 → 호스트 엔드포인트는 camelCase 유지.
        // U-Vision wire 는 DTO [JsonPropertyName] 으로 snake_case 자기서술(전역 정책 불필요).
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var hostJson = await (await client.GetAsync("/host/probe")).Content.ReadAsStringAsync();
        Assert.Contains("\"hostValue\":\"v\"", hostJson); // camelCase 보존(오염 없음)
    }

    [Fact]
    public async Task Spa_Fallback_Returns_Index_With_Config_Injected()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var res = await client.GetAsync("/u-vision/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("window.__UVISION_CONFIG__", html);
        Assert.Contains("\"apiBase\":\"/api/u-vision\"", html);
    }

    [Fact]
    public async Task Sibling_Prefix_Path_Is_Not_Swallowed_By_BasePath()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var res = await client.GetAsync("/u-visionary");   // shares /u-vision prefix but is a distinct host route
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);   // falls through to host endpoint, not SPA-swallowed
    }

    [Fact]
    public async Task Spa_Index_Is_Served_No_Cache()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var res = await client.GetAsync("/u-vision/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(
            res.Headers.TryGetValues("Cache-Control", out var values)
            && values.Any(v => v.Contains("no-cache", StringComparison.OrdinalIgnoreCase)),
            "index.html must be served with Cache-Control: no-cache");
    }

    [Fact]
    public async Task Embedded_Asset_Is_Served_With_ContentType()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var client = host.GetTestServer().CreateClient();

        var res = await client.GetAsync("/u-vision/assets/index.js");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/javascript", res.Content.Headers.ContentType?.MediaType);
        Assert.True((await res.Content.ReadAsByteArrayAsync()).Length > 0);
    }
}
