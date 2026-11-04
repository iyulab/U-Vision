using System.Globalization;
using UVision.Edge.Agent.Worker;

namespace UVision.Edge.Agent.Host;

/// <summary>프로세스 종료 코드 — 오케스트레이터(컨테이너 재시작 정책)와 운영자가 원인을 가르는 용도.</summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>설정이 잘못됐거나, 등록돼 있지 않은데 등록 토큰도 없다 — 다시 띄워도 같다.</summary>
    public const int Config = 2;

    /// <summary>엔진이 이 노드의 자격을 거절했다(회수) — 다시 띄워도 같고, 사람이 새로 등록해야 한다.</summary>
    public const int Revoked = 3;

    /// <summary>등록 토큰을 엔진이 받지 않았다(틀림 · 이미 씀 · 만료) — 새 토큰이 필요하다.</summary>
    public const int RegistrationRejected = 4;
}

/// <summary>
/// 에이전트 호스트 — 설정 한 벌로 outbox · 에이전트를 열고, 필요하면 등록하고, 취소될 때까지 사이클을 돌린다.
/// 사이클마다 <c>health</c> 파일을 갱신한다(<see cref="HealthFile"/>) — 컨테이너의 HEALTHCHECK 가 «루프가 살아 있는가» 만 본다.
/// 엔진이 안 닿는 것은 건강 문제가 아니다(에이전트는 쌓아 두고 기다리는 게 일이다) — 노드 목록의 «연락 끊김» 이 그 신호다.
/// </summary>
public static class AgentHost
{
    public const string HealthFileName = "health";

    /// <summary>
    /// 에이전트를 돌린다. 취소(SIGTERM · Ctrl+C)로 끝나면 <see cref="ExitCodes.Ok"/>. 시험은 <paramref name="http"/> 와 시계를 바꿔 끼운다.
    /// </summary>
    public static async Task<int> RunAsync(HostOptions options, HttpClient http, TextWriter log, CancellationToken ct, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        void Log(string message) => log.WriteLine($"{clock.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ} {message}");

        if (options.SendsCredentialsInTheClear)
            Log("경고: 엔진 주소가 http:// 입니다 — 노드 자격이 암호화 없이 오갑니다. 같은 기계 밖이면 https:// 를 쓰세요");

        Directory.CreateDirectory(options.StateDir);
        using var outbox = options.OutboxMaxBytes is { } max
            ? AgentOutbox.Open(options.OutboxDir, max, clock)
            : AgentOutbox.Open(options.OutboxDir, clock: clock);
        var agent = new EdgeAgent(
            new EngineClient(http, options.ApiBase, clock), outbox, options.StateDir,
            new AgentOptions { PollInterval = options.PollInterval }, clock);
        var healthPath = Path.Combine(options.StateDir, HealthFileName);

        Log($"에이전트 {EdgeAgent.AgentVersion ?? "?"} 시작 — 엔진 {options.EngineUrl} · 상태 {options.StateDir} · outbox {options.OutboxDir}");

        if (!agent.IsRegistered)
        {
            if (options.RegistrationToken is null)
            {
                Log("등록되어 있지 않고 UVISION_REGISTRATION_TOKEN 도 없습니다 — 엔진 콘솔의 «노드» 탭에서 토큰을 발급해 넣으세요");
                return ExitCodes.Config;
            }
            var registered = await RegisterAsync(agent, options, Log, clock, ct);
            if (registered is not null) return registered.Value;
            if (ct.IsCancellationRequested) return ExitCodes.Ok;
            Log($"등록했습니다 — 노드 {agent.NodeId}. 엔진 콘솔의 «노드» 탭에서 시나리오를 배정하세요");
        }
        else if (options.RegistrationToken is not null)
        {
            Log("이미 등록되어 있어 UVISION_REGISTRATION_TOKEN 은 쓰지 않습니다 — 환경에서 지워도 됩니다");
        }

        // 추론 워커 수신(원장 D-105) — 규칙은 엔진이 안 닿아도 디스크에 쥔 마지막 구성으로 돈다.
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        RuleRunner? runner = null;
        Task? receive = null;
        if (options.WorkerSocket is { } socketPath)
        {
            runner = new RuleRunner(outbox);
            ConfigureRules(runner, agent.Config, Log);
            var socketServer = new WorkerSocketServer(socketPath, runner,
                options.WorkerSocketMode is { } mode ? new WorkerSocketOptions { FileMode = mode } : null, clock);
            bool ConfigStale() => agent.ConfigConfirmedAt is { } at && clock.GetUtcNow() - at > options.MaxConfigAge;
            runner.IsConfigStale = ConfigStale;
            agent.WorkerStatus = () => WorkerStatusReport.From(socketServer.Stats, runner, ConfigStale());
            receive = socketServer.RunAsync(receiveCts.Token);
            if (receive.IsFaulted)   // 바인딩 실패(경로 · 권한)는 첫 await 전에 난다 — 다시 띄워도 같다
            {
                Log($"워커 소켓을 열지 못했습니다({receive.Exception?.GetBaseException().Message}) — UVISION_WORKER_SOCKET 경로와 권한을 확인하세요");
                return ExitCodes.Config;
            }
            Log($"워커 소켓 {socketPath} 를 듣습니다 — 규칙 {runner.RuleCount}개");
        }

        try
        {
            var wasOffline = false;
            while (!ct.IsCancellationRequested)
            {
                CycleReport report;
                try { report = await agent.RunOnceAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }   // 요청 도중 종료 신호 — 정상 종료다
                if (ct.IsCancellationRequested) break;
                TouchHealth(healthPath, clock);

                if (report.Revoked)
                {
                    Log("엔진이 이 노드의 자격을 거절했습니다(회수됨) — 멈춥니다. outbox 는 그대로 남깁니다. 새 토큰으로 다시 등록하세요");
                    return ExitCodes.Revoked;
                }
                if (report.Offline && !wasOffline) Log($"엔진에 닿지 못합니다 — 쌓아 두고 {report.NextDelay.TotalSeconds:0}초 뒤 다시 시도합니다");
                if (!report.Offline && wasOffline) Log("엔진에 다시 닿았습니다");
                wasOffline = report.Offline;
                if (report.ConfigChanged)
                {
                    Log($"구성을 받았습니다 — 시나리오 {agent.Config?.Scenarios.Count ?? 0}개 (버전 {agent.Config?.Version})");
                    if (runner is not null) ConfigureRules(runner, agent.Config, Log);
                }
                if (receive is { IsFaulted: true })
                {
                    Log($"워커 소켓 수신이 멈췄습니다({receive.Exception?.GetBaseException().Message}) — 멈춥니다");
                    return ExitCodes.Config;
                }
                if (report.Sent > 0 || report.Parked > 0)
                    Log($"전달 {report.Sent}건 (중복 응답 {report.Duplicates}) · 따로 둔 {report.Parked}건");

                try { await Task.Delay(report.NextDelay, clock, ct); }
                catch (OperationCanceledException) { break; }
            }

            Log("종료 신호를 받아 멈춥니다");
            return ExitCodes.Ok;
        }
        finally
        {
            await receiveCts.CancelAsync();
            if (receive is not null)
                try { await receive; } catch (Exception ex) when (ex is not OperationCanceledException) { /* 위에서 이미 알렸다 */ }
        }
    }

    private static void ConfigureRules(RuleRunner runner, EdgeConfig? config, Action<string> log)
    {
        runner.Configure(config);
        log($"규칙 {runner.RuleCount}개 · 카메라 {string.Join(", ", runner.CameraRefs.Order())}");
        foreach (var skipped in runner.Skipped)
            log($"규칙을 건너뜁니다 — {skipped}");
    }

    /// <summary>
    /// 등록 — 엔진이 안 닿으면(기동 순서 · 일시 장애) 지수 백오프로 계속 시도하고, 토큰을 거절하면 곧바로 끝낸다(다시 해도 같다).
    /// 성공하면 null, 끝내야 하면 종료 코드, 취소되면 null 이고 호출부가 <paramref name="ct"/> 로 가른다.
    /// </summary>
    private static async Task<int?> RegisterAsync(
        EdgeAgent agent, HostOptions options, Action<string> log, TimeProvider clock, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await agent.RegisterAsync(options.RegistrationToken!, options.NodeName, ct);
                return null;
            }
            catch (CredentialsRejectedException)
            {
                log("엔진이 등록 토큰을 받지 않았습니다(틀렸거나 이미 썼거나 만료) — 콘솔의 «노드» 탭에서 새 토큰을 발급하세요");
                return ExitCodes.RegistrationRejected;
            }
            catch (PermanentRejectionException ex)
            {
                log($"등록 요청이 거절됐습니다: {ex.Message}");
                return ExitCodes.Config;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                log($"엔진에 등록하지 못했습니다({ex.GetType().Name}) — {delay.TotalSeconds:0}초 뒤 다시 시도합니다");
                try { await Task.Delay(delay, clock, ct); }
                catch (OperationCanceledException) { return null; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
        return null;
    }

    private static void TouchHealth(string path, TimeProvider clock)
    {
        // 임시 파일에 쓰고 교체한다 — 컨테이너 HEALTHCHECK 가 같은 순간 읽어도 빈 파일 · 반쪽 파일을 보지 않는다. Windows 는 읽는 쪽이 파일을 연 동안의
        // 덮어쓰기 이동을 거절하므로 짧게 다시 시도한다(Linux 의 rename 은 원자적이라 첫 시도에 끝난다).
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture));
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(attempt < 5 ? 1 : 10); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            /* 건강 표지를 못 쓰면 다음 사이클에 다시 — 전달이 이것 때문에 멈추지 않는다. */
        }
    }

    /// <summary>
    /// HEALTHCHECK 용 — 건강 파일이 <paramref name="maxAge"/> 안에 갱신됐으면 true. 파일이 없거나 낡으면 루프가 멈춘 것이다.
    /// </summary>
    public static bool IsHealthy(string stateDir, TimeSpan maxAge, TimeProvider? clock = null)
    {
        var path = Path.Combine(stateDir, HealthFileName);
        // 교체되는 순간에 열기가 거절될 수 있다(Windows) — 몇 밀리초 안에 끝나므로 짧게 다시 읽는다. 파일이 없거나 낡거나 읽을 수 없는 것이
        // 끝까지 이어질 때만 «건강하지 않다».
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var written = DateTimeOffset.Parse(reader.ReadToEnd(), CultureInfo.InvariantCulture);
                return (clock ?? TimeProvider.System).GetUtcNow() - written <= maxAge;
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
            {
                if (attempt >= 20) return false;
                Thread.Sleep(attempt < 5 ? 1 : 10);
            }
        }
    }
}
