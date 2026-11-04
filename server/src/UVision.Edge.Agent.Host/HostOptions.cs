using System.Globalization;

namespace UVision.Edge.Agent.Host;

/// <summary>
/// 컨테이너 안 에이전트 프로세스의 설정 — 전부 환경 변수다(컨테이너 관례). 파싱은 순수 함수라 환경을 직접 읽지 않고 사전을 받는다.
/// 모르는 값 · 범위 밖은 기동에서 거부한다 — 현장에서 «조용히 기본값으로 도는» 에이전트가 가장 찾기 어렵다.
/// </summary>
public sealed record HostOptions
{
    public const string DefaultApiBasePath = "/api/u-vision";
    public const string DefaultStateDir = "/data/state";
    public const string DefaultOutboxDir = "/data/outbox";

    /// <summary>엔진 주소(스킴 포함, 경로 없음) — 예: <c>https://engine.example:8080</c>.</summary>
    public required Uri EngineUrl { get; init; }

    /// <summary>엔진 API 기본 경로. 엔진을 다른 경로에 마운트했을 때만 바꾼다.</summary>
    public string ApiBasePath { get; init; } = DefaultApiBasePath;

    /// <summary>일회용 등록 토큰 — 아직 등록하지 않았을 때만 쓴다. 등록한 뒤에는 없어도 된다(있어도 무시, 로그에 남기지 않음).</summary>
    public string? RegistrationToken { get; init; }

    public string? NodeName { get; init; }

    public string StateDir { get; init; } = DefaultStateDir;

    public string OutboxDir { get; init; } = DefaultOutboxDir;

    /// <summary>
    /// 추론 워커가 붙는 유닉스 소켓 파일의 경로(절대 경로, 공유 볼륨 안) — 비어 있으면 워커를 받지 않는다(스트림 감시 시나리오를 돌리지 않는 노드).
    /// 소켓 경로는 운영체제 한도(약 104자)가 있다.
    /// </summary>
    public string? WorkerSocket { get; init; }

    /// <summary>
    /// 소켓 파일의 Unix 권한. 비어 있으면 소유자만(<c>0600</c>). 워커가 다른 uid 의 컨테이너면 공유 볼륨의 그룹으로 맞춰 넓힌다
    /// (<c>0660</c> + 워커에 그 그룹) — 같은 볼륨을 나누는 컨테이너가 소켓을 열 수 있는 유일한 길이다.
    /// </summary>
    public UnixFileMode? WorkerSocketMode { get; init; }

    /// <summary>
    /// 구성을 엔진과 마지막으로 확인한 뒤 이만큼 지나면 «낡았다» — 규칙은 계속 돌되 사건에 <c>config_stale: true</c> 가 달린다(원장 D-105 W-6).
    /// 기본 24 시간.
    /// </summary>
    public TimeSpan MaxConfigAge { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>outbox 가 디스크에서 차지할 수 있는 상한(바이트) — 넘으면 오래된 크롭부터 버린다. 0 이하면 라이브러리 기본.</summary>
    public long? OutboxMaxBytes { get; init; }

    public string ApiBase => EngineUrl.ToString().TrimEnd('/') + ApiBasePath;

    /// <summary>환경 변수 사전에서 설정을 읽는다. 잘못된 값은 <see cref="HostConfigException"/>(모든 문제를 한 번에 알린다).</summary>
    public static HostOptions FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        var problems = new List<string>();
        string? Get(string key) => env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        Uri? url = null;
        if (Get("UVISION_ENGINE_URL") is not { } rawUrl) problems.Add("UVISION_ENGINE_URL 이 필요합니다 (예: https://engine.example:8080)");
        else if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out url) || url.Scheme is not ("http" or "https"))
        {
            problems.Add($"UVISION_ENGINE_URL 은 http:// 또는 https:// 로 시작하는 주소여야 합니다: {rawUrl}");
            url = null;
        }
        else if (url.AbsolutePath.Length > 1 || url.Query.Length > 0)
        {
            problems.Add("UVISION_ENGINE_URL 에는 경로를 넣지 않습니다 — 엔진 API 경로는 UVISION_API_BASE_PATH 로 정합니다");
            url = null;
        }

        var apiBase = Get("UVISION_API_BASE_PATH") ?? DefaultApiBasePath;
        if (!apiBase.StartsWith('/') || apiBase.Length > 1 && apiBase.EndsWith('/') || apiBase.Contains(' '))
            problems.Add("UVISION_API_BASE_PATH 는 '/' 로 시작하고 '/' 로 끝나지 않는 경로여야 합니다");

        var poll = TimeSpan.FromSeconds(30);
        if (Get("UVISION_POLL_SECONDS") is { } rawPoll)
        {
            if (!int.TryParse(rawPoll, NumberStyles.None, CultureInfo.InvariantCulture, out var secs) || secs is < 5 or > 3600)
                problems.Add($"UVISION_POLL_SECONDS 는 5~3600 사이의 정수여야 합니다: {rawPoll}");
            else poll = TimeSpan.FromSeconds(secs);
        }

        long? maxBytes = null;
        if (Get("UVISION_OUTBOX_MAX_MB") is { } rawMax)
        {
            if (!long.TryParse(rawMax, NumberStyles.None, CultureInfo.InvariantCulture, out var mb) || mb is < 1 or > 10_000_000)
                problems.Add($"UVISION_OUTBOX_MAX_MB 는 1 이상의 정수(MiB)여야 합니다: {rawMax}");
            else maxBytes = mb * 1024 * 1024;
        }

        var maxConfigAge = TimeSpan.FromHours(24);
        if (Get("UVISION_MAX_CONFIG_AGE_HOURS") is { } rawAge)
        {
            if (!int.TryParse(rawAge, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours is < 1 or > 8760)
                problems.Add($"UVISION_MAX_CONFIG_AGE_HOURS 는 1~8760 사이의 정수(시간)여야 합니다: {rawAge}");
            else maxConfigAge = TimeSpan.FromHours(hours);
        }

        var workerSocket = Get("UVISION_WORKER_SOCKET");
        if (workerSocket is not null && (!Path.IsPathRooted(workerSocket) || workerSocket.Length > 100 || workerSocket.EndsWith('/') || workerSocket.EndsWith('\\')))
            problems.Add("UVISION_WORKER_SOCKET 은 소켓 파일의 절대 경로여야 합니다(100자 이하, 디렉터리가 아니라 파일 — 예: /data/worker/worker.sock)");

        UnixFileMode? workerSocketMode = null;
        if (Get("UVISION_WORKER_SOCKET_MODE") is { } rawMode)
        {
            // 8 진수 세 자리(또는 앞에 0 을 붙인 네 자리). 소유자가 읽고 쓸 수 없는 값은 에이전트 자신이 소켓을 못 쓰는 값이라 거부한다.
            var octal = rawMode.Length is 3 or 4 && rawMode.All(c => c is >= '0' and <= '7') && (rawMode.Length == 3 || rawMode[0] == '0');
            if (workerSocket is null)
                problems.Add("UVISION_WORKER_SOCKET_MODE 는 UVISION_WORKER_SOCKET 과 함께만 쓸 수 있습니다");
            else if (!octal || (Convert.ToInt32(rawMode, 8) & 0b110_000_000) != 0b110_000_000)
                problems.Add($"UVISION_WORKER_SOCKET_MODE 는 소유자가 읽고 쓸 수 있는 8 진수 권한(예: 0600 · 0660)이어야 합니다: {rawMode}");
            else workerSocketMode = (UnixFileMode)Convert.ToInt32(rawMode, 8);
        }

        var name = Get("UVISION_NODE_NAME");
        if (name is { Length: > 80 } || name?.Any(char.IsControl) == true)
            problems.Add("UVISION_NODE_NAME 은 제어 문자 없는 80자 이하여야 합니다");

        if (problems.Count > 0 || url is null) throw new HostConfigException(problems);

        return new HostOptions
        {
            EngineUrl = url,
            ApiBasePath = apiBase,
            RegistrationToken = Get("UVISION_REGISTRATION_TOKEN"),
            NodeName = name,
            StateDir = Get("UVISION_STATE_DIR") ?? DefaultStateDir,
            OutboxDir = Get("UVISION_OUTBOX_DIR") ?? DefaultOutboxDir,
            WorkerSocket = workerSocket,
            WorkerSocketMode = workerSocketMode,
            MaxConfigAge = maxConfigAge,
            PollInterval = poll,
            OutboxMaxBytes = maxBytes,
        };
    }

    /// <summary>자격이 평문(http)으로 오가는 설정인가 — 같은 기계의 loopback 이 아니면 true (경고용).</summary>
    public bool SendsCredentialsInTheClear => EngineUrl.Scheme == "http" && !EngineUrl.IsLoopback;
}

/// <summary>설정이 잘못됐다 — <see cref="Problems"/> 에 고칠 것을 모두 담는다.</summary>
public sealed class HostConfigException(IReadOnlyList<string> problems) : Exception(string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
