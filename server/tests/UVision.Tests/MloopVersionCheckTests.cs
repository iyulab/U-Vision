using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UVision.Api.Configuration;
using UVision.Api.Services.Mloop;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// mloop serve 판 확인 — <c>/health</c> 의 <c>version</c> 판정(순수)과, 기동 뒤 한 번 도는 확인이 무엇을 로그로 남기는지.
/// 판정 경로 · 기동은 건드리지 않는다(로그만).
/// </summary>
public class MloopVersionCheckTests
{
    [Theory]
    [InlineData("""{"status":"healthy","version":"0.45.0+7ec23becb602"}""", MloopVersion.Status.Supported, "0.45.0+7ec23becb602")]
    [InlineData("""{"status":"healthy","version":"0.46.1"}""", MloopVersion.Status.Supported, "0.46.1")]
    [InlineData("""{"status":"healthy","version":"1.0.0-rc.1"}""", MloopVersion.Status.Supported, "1.0.0-rc.1")]
    [InlineData("""{"status":"healthy","version":"0.44.0+abc"}""", MloopVersion.Status.TooOld, "0.44.0+abc")]
    [InlineData("""{"status":"healthy"}""", MloopVersion.Status.Unknown, null)]
    [InlineData("""{"version":"dev"}""", MloopVersion.Status.Unknown, "dev")]
    [InlineData("not json", MloopVersion.Status.Unknown, null)]
    [InlineData(null, MloopVersion.Status.Unknown, null)]
    public void Judge_ReadsVersion(string? body, MloopVersion.Status expected, string? reported)
    {
        var (status, version) = MloopVersion.Judge(body);
        Assert.Equal(expected, status);
        Assert.Equal(reported, version);
    }

    [Fact]
    public async Task OldServer_IsWarnedOnce_ForBothUsesOfTheSameEndpoint()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"healthy","version":"0.44.0"}"""),
        });
        var log = new ListLogger();
        await RunAsync(
            new MlOptions { Provider = "mloop", Endpoint = "http://mloop.test/" },
            new MemoryOptions { Provider = "mloop", Endpoint = "http://mloop.test", Encoder = "e" },
            handler, log);

        Assert.Equal(["http://mloop.test/health"], handler.Requested); // 같은 서버는 한 번만 묻는다
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("0.44.0", warning.Message);
        Assert.Contains("ML 분류기 · 기억 단", warning.Message);
    }

    [Fact]
    public async Task SupportedServer_IsInformation_Only()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"healthy","version":"0.45.0+7ec23be"}"""),
        });
        var log = new ListLogger();
        await RunAsync(new MlOptions { Provider = "mloop", Endpoint = "http://mloop.test" }, new MemoryOptions(), handler, log);

        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Message.Contains("0.45.0+7ec23be"));
    }

    [Fact]
    public async Task UnreachableServer_DoesNotWarn_OrThrow()
    {
        // MLoop 이 U-Vision 보다 늦게 뜰 수 있다 — 닿지 않으면 정보로만 남긴다.
        var handler = new Handler(_ => throw new HttpRequestException("connection refused"));
        var log = new ListLogger();
        await RunAsync(new MlOptions { Provider = "mloop", Endpoint = "http://mloop.test" }, new MemoryOptions(), handler, log);

        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Message.Contains("connection refused"));
    }

    [Fact]
    public async Task NoMloopProvider_AsksNothing()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not be called"));
        var log = new ListLogger();
        await RunAsync(new MlOptions(), new MemoryOptions(), handler, log);

        Assert.Empty(handler.Requested);
        Assert.Empty(log.Entries);
    }

    private static async Task RunAsync(MlOptions ml, MemoryOptions memory, Handler handler, ListLogger log)
    {
        var service = new MloopVersionCheckService(ml, memory, new Factory(handler), log);
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ListLogger : ILogger<MloopVersionCheckService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
