using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UVision.Api;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// OPC UA 서버 기동(P4 사건 전송 ①) — 기본 꺼짐, 켜면 서버가 서고 인증서 폴더가 데이터 루트 아래 생긴다.
/// 이 시험은 «서버가 서고 TCP 로 닿는다» 까지다 — 프로토콜 왕복(인증서 신뢰 · 구독 · 메서드)은 <c>OpcUaResultTests</c> 가 한다.
/// </summary>
public class OpcUaServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvision-opcua-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 서버가 막 내려가는 중이면 임시 폴더 정리는 다음에 — 시험 결과와 무관.
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private IHost Build(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        // 기본 로깅 제공자(Windows 는 이벤트 로그 포함)를 비운다 — 호스트를 해제하는 순간에도 OPC UA 수신 스레드가 로그를 남기는데, 이미 해제된
        // 이벤트 로그 제공자에 쓰면 `Logger.Log` 가 AggregateException 을 던지고 입출력 완료 스레드에서 처리되지 않아 시험 프로세스가 통째로 죽는다
        // (스위트 중간에 «534 건에서 멈춘 채 FAIL» 로 보이던 플레이크의 원인). 같은 파일의 다른 OPC UA 호스트도 이미 비운다.
        builder.Logging.ClearProviders();
        settings["UVision:Storage:DataPath"] = _root;
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddUVisionOpcUa(builder.Configuration, _root);
        return builder.Build();
    }

    private static async Task<bool> CanConnectAsync(int port, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(200);
            }
        }
        return false;
    }

    [Fact]
    public async Task Disabled_By_Default_Opens_No_Port_And_Makes_No_Pki_Folder()
    {
        var port = FreePort();
        using var host = Build(new() { ["UVision:Events:OpcUa:Port"] = port.ToString() });
        await host.StartAsync();
        Assert.False(await CanConnectAsync(port, TimeSpan.FromSeconds(1)));
        Assert.False(Directory.Exists(Path.Combine(_root, "opcua")));
        await host.StopAsync();
    }

    [Fact]
    public async Task Enabled_Starts_A_Server_And_Creates_Its_Certificate_Under_The_Data_Root()
    {
        var port = FreePort();
        using var host = Build(new()
        {
            ["UVision:Events:OpcUa:Enabled"] = "true",
            ["UVision:Events:OpcUa:Port"] = port.ToString(),
            ["UVision:Events:OpcUa:Host"] = "localhost",
        });
        await host.StartAsync();
        try
        {
            Assert.True(await CanConnectAsync(port, TimeSpan.FromSeconds(30)), "OPC UA server never listened");
            // 스택의 배치: 인스턴스 인증서(공개)는 pki/certs, 개인 키는 pki/private — 둘 다 데이터 루트 아래에 생긴다.
            var pki = Path.Combine(_root, "opcua", "pki");
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(pki, "certs"), "*.der"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(pki, "private"), "*.pfx"));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData("Port", "0")]
    [InlineData("Port", "70000")]
    [InlineData("ResultWindow", "0")]
    public void Bad_Settings_Stop_Startup_Even_When_Disabled(string key, string value)
    {
        var settings = new Dictionary<string, string?> { ["UVision:Events:OpcUa:" + key] = value };
        Assert.Throws<InvalidOperationException>(() => Build(settings));
    }
}
