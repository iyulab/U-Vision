using System.Collections;
using System.Runtime.InteropServices;
using UVision.Edge.Agent;

namespace UVision.Edge.Agent.Host;

// uvision-edge-agent            — 에이전트를 돌린다(환경 변수로 설정)
// uvision-edge-agent health     — HEALTHCHECK: 루프가 살아 있으면 0, 아니면 1
// uvision-edge-agent version    — 판을 찍고 끝낸다
//
// 최상위 문(statement)이 아니라 이름 있는 클래스로 둔다 — 최상위 문은 전역 `Program` 을 만들어 시험 프로젝트가 엔진의 `Program`
// 과 이 호스트의 `Program` 을 함께 참조할 때 모호해진다.
internal static class HostProgram
{
    /// <summary>HEALTHCHECK 가 «멈췄다» 고 보는 나이: 폴링 간격의 3 배, 단 엔진이 안 닿을 때의 백오프 상한(5 분)을 두 번 지나는 값 아래로는 줄이지 않는다.</summary>
    internal static TimeSpan HealthMaxAge(int pollSeconds) => TimeSpan.FromSeconds(Math.Max(pollSeconds * 3, 2 * 300 + 30));

    internal static async Task<int> Main(string[] args)
    {
        var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value as string, StringComparer.Ordinal);

        switch (args.FirstOrDefault())
        {
            case "version":
                Console.WriteLine(EdgeAgent.AgentVersion ?? "unknown");
                return 0;

            case "health":
            {
                var stateDir = env.GetValueOrDefault("UVISION_STATE_DIR") ?? HostOptions.DefaultStateDir;
                var poll = int.TryParse(env.GetValueOrDefault("UVISION_POLL_SECONDS"), out var secs) && secs is >= 5 and <= 3600 ? secs : 30;
                return AgentHost.IsHealthy(stateDir, HealthMaxAge(poll)) ? 0 : 1;
            }

            case null:
            case "run":
                break;

            default:
                Console.Error.WriteLine($"모르는 명령: {args[0]}  (run | health | version)");
                return ExitCodes.Config;
        }

        HostOptions options;
        try { options = HostOptions.FromEnvironment(env); }
        catch (HostConfigException ex)
        {
            Console.Error.WriteLine("설정이 잘못됐습니다:");
            foreach (var problem in ex.Problems) Console.Error.WriteLine($"  - {problem}");
            return ExitCodes.Config;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"uvision-edge-agent/{EdgeAgent.AgentVersion ?? "dev"}");
        return await AgentHost.RunAsync(options, http, Console.Out, cts.Token);
    }
}
