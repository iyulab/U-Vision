using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using UVision.Edge.Agent.Worker;

namespace UVision.Edge.Agent;

/// <summary>에이전트 동작 조정값 — 기본값은 현장에서 흔한 값이고, 시험은 짧게 줄여 쓴다.</summary>
public sealed class AgentOptions
{
    /// <summary>구성 폴링 간격(성공했을 때 다음 사이클까지).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>한 번에 엔진으로 보내는 사건 수(엔진 상한 500 아래).</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>끊겼을 때 첫 재시도 간격 — 실패마다 두 배로, <see cref="BackoffCap"/> 까지.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>백오프에 더하는 무작위 흔들림 비율(0~1) — 여러 노드가 한꺼번에 몰리지 않게. 시험은 0.</summary>
    public double Jitter { get; set; } = 0.2;
}

/// <summary>사이클 한 번의 결과 — 호출부(루프 · 시험)가 다음 동작을 정한다.</summary>
/// <param name="Sent">엔진이 받았거나 이미 갖고 있어 outbox 에서 지운 항목 수.</param>
/// <param name="Duplicates">그 가운데 엔진이 <c>duplicate</c> 라 한 수(응답을 잃고 다시 보낸 경우).</param>
/// <param name="Parked">영구 거절돼 따로 둔 항목 수.</param>
/// <param name="ConfigChanged">이번에 새 구성을 받았는가.</param>
/// <param name="Offline">엔진에 닿지 못해(또는 일시 오류) 멈췄는가 — 항목은 그대로 남아 있다.</param>
/// <param name="Revoked">엔진이 자격을 거절했는가 — 에이전트는 더 보내지 않는다.</param>
/// <param name="NextDelay">다음 사이클까지 기다릴 시간.</param>
public sealed record CycleReport(
    int Sent, int Duplicates, int Parked, bool ConfigChanged, bool Offline, bool Revoked, TimeSpan NextDelay);

/// <summary>
/// 엣지 에이전트 코어(원장 D-102) — 엔진으로 <b>나가는 방향</b>으로만 연결한다. 등록(일회용 토큰 → 노드별 비밀을 디스크에 보관),
/// 구성 폴링(<c>ETag</c> 조건부, 받은 사본을 디스크에 두어 <b>오프라인에서도 그것으로 돈다</b>), outbox 비우기(순서대로, 최소 한 번),
/// 서버 시계 오프셋 추정(<c>Date</c>)을 한 사이클(<see cref="RunOnceAsync"/>)에 담는다.
/// <list type="bullet">
/// <item>엔진이 안 닿으면 항목은 그대로 남고 지수 백오프로 다시 시도한다. 응답을 잃고 다시 보낸 항목은 엔진이 <c>duplicate</c> 로 답해 한 번만 남는다.</item>
/// <item>엔진이 자격을 거절하면(회수) 더는 보내지 않고 <see cref="Revoked"/> 로 멈춘다 — outbox 는 지우지 않는다.</item>
/// <item>엔진이 항목을 영구 거절하면(<c>rejected</c>) 다시 보내지 않고 <c>parked/</c> 에 사유와 함께 둔다.</item>
/// </list>
/// 한 사이클은 직렬이다(동시에 두 사이클을 돌리지 않는다).
/// </summary>
public sealed class EdgeAgent
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly EngineClient _engine;
    private readonly AgentOutbox _outbox;
    private readonly string _stateDir;
    private readonly TimeProvider _clock;
    private readonly AgentOptions _options;
    private readonly Random _random;
    private readonly ClockOffsetEstimator _offset = new();
    private int _failures;
    private bool _statusUnsupported;

    /// <summary>
    /// 상태 보고에 싣는 추론 워커 수신 상태를 만드는 함수 — 워커 소켓을 켠 호스트가 정한다. 없으면(또는 null 을 돌려주면) 보고에 <c>worker</c> 가 없다.
    /// </summary>
    public Func<WorkerStatusReport?>? WorkerStatus { get; set; }

    public EdgeAgent(EngineClient engine, AgentOutbox outbox, string stateDir, AgentOptions? options = null, TimeProvider? clock = null, Random? random = null)
    {
        _engine = engine;
        _outbox = outbox;
        _stateDir = Path.GetFullPath(stateDir);
        _options = options ?? new AgentOptions();
        _clock = clock ?? TimeProvider.System;
        _random = random ?? Random.Shared;
        Directory.CreateDirectory(_stateDir);
        Credentials = Load<StoredCredentials>(CredentialsFile);
        Config = Load<EdgeConfig>(ConfigFile);
        Revoked = File.Exists(RevokedFile);
    }

    private sealed record StoredCredentials(
        [property: JsonPropertyName("node_id")] string NodeId,
        [property: JsonPropertyName("secret")] string Secret)
    {
        public string Bearer => $"{NodeId}.{Secret}";
    }

    private string CredentialsFile => Path.Combine(_stateDir, "credentials.json");
    private string ConfigFile => Path.Combine(_stateDir, "config.json");
    private string RevokedFile => Path.Combine(_stateDir, "revoked");

    private StoredCredentials? Credentials { get; set; }

    /// <summary>등록돼 있는가(자격이 디스크에 있다).</summary>
    public bool IsRegistered => Credentials is not null;

    public string? NodeId => Credentials?.NodeId;

    /// <summary>마지막으로 받은 구성 — 디스크에 있어 재시작 뒤, 엔진이 안 닿아도 그대로 쓴다. 한 번도 못 받았으면 null.</summary>
    public EdgeConfig? Config { get; private set; }

    /// <summary>엔진이 자격을 거절했는가(회수) — 새로 등록하기 전까지 유지된다(재시작에도).</summary>
    public bool Revoked { get; private set; }

    /// <summary>지금까지 잰 서버 시계 오프셋(밀리초) — 사건 배치에 실어 보낸다. 아직 못 쟀으면 null.</summary>
    public long? ClockOffsetMs => _offset.OffsetMs();

    /// <summary>엔진에 알리는 에이전트 판 — 어셈블리 정보 판(커밋 해시 접미사는 뗀다).</summary>
    public static string? AgentVersion { get; } =
        typeof(EdgeAgent).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            is { Length: > 0 } v ? v.Split('+')[0] : null;

    /// <summary>일회용 토큰으로 등록하고 자격을 디스크에 보관한다(Unix 에서는 소유자만 읽게).</summary>
    public async Task RegisterAsync(string token, string? name, CancellationToken ct)
    {
        var result = await _engine.RegisterAsync(token, name, ct);
        Credentials = new StoredCredentials(result.NodeId, result.Secret);
        Save(CredentialsFile, Credentials, ownerOnly: true);
        if (File.Exists(RevokedFile)) File.Delete(RevokedFile);
        Revoked = false;
        _failures = 0;
    }

    /// <summary>사이클 한 번 — 구성 폴링, 그다음 outbox 비우기. 예외로 끝나지 않는다(네트워크 실패는 <see cref="CycleReport.Offline"/>).</summary>
    public async Task<CycleReport> RunOnceAsync(CancellationToken ct)
    {
        if (Credentials is null) throw new InvalidOperationException("등록되지 않았다 — RegisterAsync 를 먼저");
        if (Revoked) return new CycleReport(0, 0, 0, false, false, true, _options.BackoffCap);

        var configChanged = false;
        try
        {
            var poll = await _engine.GetConfigAsync(Credentials.Bearer, Config?.Version, ct);
            if (poll.ServerDate is { } serverDate) _offset.Observe(poll.SentAt, poll.ReceivedAt, serverDate);
            if (poll.Config is not null)
            {
                Config = poll.Config;
                Save(ConfigFile, poll.Config);
                configChanged = true;
            }
            var (sent, duplicates, parked) = await FlushAsync(ct);
            _failures = 0;
            await ReportStatusAsync(ct);
            return new CycleReport(sent, duplicates, parked, configChanged, false, false, _options.PollInterval);
        }
        catch (CredentialsRejectedException)
        {
            Revoked = true;
            File.WriteAllText(RevokedFile, _clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture));
            return new CycleReport(0, 0, 0, configChanged, false, true, _options.BackoffCap);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            _failures++;
            return new CycleReport(0, 0, 0, configChanged, true, false, Backoff());
        }
    }

    /// <summary>사이클을 되풀이한다 — 취소되거나 자격이 거절되면 끝난다.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CycleReport report;
            try { report = await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }   // 요청 도중 취소 — 끝낸다(예외로 올리지 않는다)
            if (report.Revoked) return;
            try { await Task.Delay(report.NextDelay, _clock, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 자기 상태를 엔진에 알린다 — 사건 전달이 끝난 뒤의 <b>덤</b>이다. 실패해도 사이클을 오프라인으로 만들지 않고(전달은 이미 끝났다),
    /// 엔진이 이 경로를 모르면(옛 판) 이 프로세스에서는 다시 시도하지 않는다. 자격 거절만 올려 보낸다(회수는 어디서든 같은 뜻).
    /// </summary>
    private async Task ReportStatusAsync(CancellationToken ct)
    {
        if (_statusUnsupported) return;
        try { await _engine.PostStatusAsync(Credentials!.Bearer, _outbox.Stats(), AgentVersion, ct, WorkerStatus?.Invoke()); }
        catch (PermanentRejectionException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            _statusUnsupported = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or PermanentRejectionException
                                       && !ct.IsCancellationRequested)
        {
            // 보고는 다음 사이클에 다시 간다 — 여기서 잃는 것은 운영자 화면의 한 주기 지연뿐이다.
        }
    }

    /// <summary>
    /// outbox 를 오래된 것부터 비운다 — 연속된 사건은 한 배치로, 크롭은 하나씩. 첫 일시 실패에서 멈춘다(순서를 건너뛰지 않는다).
    /// </summary>
    private async Task<(int Sent, int Duplicates, int Parked)> FlushAsync(CancellationToken ct)
    {
        int sent = 0, duplicates = 0, parked = 0;
        while (true)
        {
            var items = _outbox.Peek(_options.BatchSize);
            if (items.Count == 0) return (sent, duplicates, parked);

            if (items[0].Kind == OutboxKinds.Crop)
            {
                var crop = items[0];
                var image = _outbox.ReadCrop(crop);
                if (image is null) { _outbox.Park(crop, "crop file missing"); parked++; continue; }
                try { await _engine.SendCropAsync(crop, image, ct); _outbox.Ack(crop); sent++; }
                catch (PermanentRejectionException ex) { _outbox.Park(crop, ex.Message); parked++; }
                continue;
            }

            var batch = items.TakeWhile(i => i.Kind == OutboxKinds.Event).ToList();
            EventsResponse response;
            try { response = await _engine.PostEventsAsync(Credentials!.Bearer, batch, _offset.OffsetMs(), ct); }
            catch (PermanentRejectionException ex)
            {
                // 배치 전체가 구조적으로 거절(예: 500건 초과) — 첫 항목만 따로 두고 계속하면 같은 일이 반복된다. 사유와 함께 한 건씩 둔다.
                _outbox.Park(batch[0], ex.Message); parked++; continue;
            }

            var byId = response.Results.ToDictionary(r => r.EventId, StringComparer.Ordinal);
            var progressed = false;
            foreach (var item in batch)
            {
                if (!byId.TryGetValue(item.Id, out var result)) continue; // 답이 없으면 남겨 둔다 — 다음 사이클에 다시.
                progressed = true;
                switch (result.Status)
                {
                    case "accepted": _outbox.Ack(item); sent++; break;
                    case "duplicate": _outbox.Ack(item); sent++; duplicates++; break;
                    default: _outbox.Park(item, result.Code ?? result.Status); parked++; break;
                }
            }
            if (!progressed) throw new HttpRequestException("엔진 응답에 보낸 사건의 결과가 없다");
        }
    }

    private TimeSpan Backoff()
    {
        var exp = _options.BackoffBase.TotalSeconds * Math.Pow(2, Math.Min(_failures - 1, 20));
        var capped = Math.Min(exp, _options.BackoffCap.TotalSeconds);
        var jitter = 1 + (_random.NextDouble() * 2 - 1) * _options.Jitter;
        return TimeSpan.FromSeconds(capped * jitter);
    }

    private static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private static void Save<T>(string path, T value, bool ownerOnly = false)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        if (ownerOnly && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}
