using System.Diagnostics;
using System.Text;
using Xunit;

namespace UVision.Tests;

/// <summary>
/// 설정이 잘못된 엔진은 스택 트레이스와 함께 abort(컨테이너에서 종료 코드 139)하지 않고, 사유 한 줄과 종료 코드 2 로 끝난다.
/// 실제 호스트 프로세스를 띄워 확인한다 — 인-프로세스 팩토리로는 종료 코드를 볼 수 없다.
/// </summary>
public sealed class StartupFailureTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "uvision-startfail-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private async Task<(int ExitCode, string Stderr)> RunEngineAsync(params (string Key, string Value)[] env)
    {
        var dll = typeof(Program).Assembly.Location;
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        psi.Environment["Storage__DataPath"] = _data;
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0"; // 혹시 떠도 고정 포트를 잡지 않는다
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        foreach (var (key, value) in env) psi.Environment[key] = value;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("엔진이 설정 오류에서 끝나지 않고 계속 떠 있다");
        }
        await stdout;
        return (process.ExitCode, await stderr);
    }

    [Fact]
    public async Task UnknownVlmProvider_ExitsWithCode2_AndAOneLineReason_NotAStackTrace()
    {
        var (code, stderr) = await RunEngineAsync(("VLM_PROVIDER", "nonsense"));

        Assert.Equal(2, code);
        Assert.Contains("설정이 잘못되어 시작하지 않습니다", stderr);
        Assert.Contains("알 수 없는 VLM provider: 'nonsense'", stderr);
        Assert.DoesNotContain("   at UVision", stderr);   // 스택 프레임이 아니다
        Assert.DoesNotContain("Unhandled exception", stderr);
    }
}
