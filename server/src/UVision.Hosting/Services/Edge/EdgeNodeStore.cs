using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Services.Edge;

/// <summary>
/// 엣지 노드 신원(원장 D-102 E-1) — 관리자가 <b>일회용 등록 토큰</b>을 발급하고, 에이전트가 그 토큰으로 등록해 <b>노드별 비밀</b>을
/// 받는다. 사용자 계정은 만들지 않는다(노드는 서비스 신원).
/// <list type="bullet">
/// <item>토큰 · 비밀은 높은 엔트로피(256 비트)의 무작위 값이라 <b>SHA-256 해시만</b> 둔다(PIN 처럼 느린 해시가 필요한 낮은 엔트로피가 아니다).
/// 평문은 발급·등록 응답에서만 나간다. 비교는 상수 시간.</item>
/// <item>토큰은 한 번만 쓴다 — 소비와 노드 생성은 한 잠금 안에서 일어나 같은 토큰으로 두 노드가 생기지 않는다(단일 호스트 전제).</item>
/// <item>노드는 하나씩 회수한다(<see cref="RevokeAsync"/>) — 회수된 노드의 자격은 곧바로 통하지 않는다. 기록은 남긴다.</item>
/// </list>
/// 저장: <c>{DataPath}/edge/tokens/{tokenId}.json</c> · <c>{DataPath}/edge/nodes/{nodeId}/node.json</c>(모두 atomic 쓰기).
/// </summary>
public sealed class EdgeNodeStore(StoragePaths paths, TimeProvider clock)
{
    public const int DefaultTtlMinutes = 60;
    public const int MaxTtlMinutes = 1440;
    public const int MaxNameLength = 80;
    public const int MaxAgentVersionLength = 40;

    /// <summary>노드 시계 오프셋이 이만큼(밀리초)을 넘으면 «시계 어긋남» 으로 표시한다 — 표시일 뿐 거절도 경보도 아니다.</summary>
    public const long ClockSkewThresholdMs = 60_000;

    /// <summary>마지막 접속 시각을 디스크에 다시 쓰는 최소 간격 — 폴링마다 쓰지 않는다.</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record TokenFile
    {
        [JsonPropertyName("token_hash")] public required string TokenHash { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("created_at")] public required string CreatedAt { get; init; }
        [JsonPropertyName("expires_at")] public required string ExpiresAt { get; init; }
        [JsonPropertyName("used_at")] public string? UsedAt { get; init; }
        [JsonPropertyName("node_id")] public string? NodeId { get; init; }
    }

    private sealed record NodeFile
    {
        [JsonPropertyName("node_id")] public required string NodeId { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("registered_at")] public required string RegisteredAt { get; init; }
        [JsonPropertyName("secret_hash")] public required string SecretHash { get; init; }
        [JsonPropertyName("last_seen")] public string? LastSeen { get; init; }
        [JsonPropertyName("scenarios")] public IReadOnlyList<string> Scenarios { get; init; } = [];
        [JsonPropertyName("clock_offset_ms")] public long? ClockOffsetMs { get; init; }
        [JsonPropertyName("status")] public EdgeNodeStatus? Status { get; init; }
        [JsonPropertyName("revoked_at")] public string? RevokedAt { get; init; }

        public EdgeNode ToNode() => new()
        {
            NodeId = NodeId, Name = Name, RegisteredAt = RegisteredAt, LastSeen = LastSeen, RevokedAt = RevokedAt,
            Scenarios = Scenarios, ClockOffsetMs = ClockOffsetMs, Status = Status,
            ClockSkewed = ClockOffsetMs is { } offset ? Math.Abs(offset) > ClockSkewThresholdMs : null,
        };
    }

    /// <summary>일회용 등록 토큰을 발급한다. <paramref name="name"/> 은 등록되는 노드의 기본 표시 이름.</summary>
    /// <exception cref="ArgumentException">이름·유효 시간이 범위를 벗어남.</exception>
    public async Task<EdgeTokenResponse> IssueTokenAsync(string? name, int? ttlMinutes, CancellationToken ct)
    {
        var cleanName = CleanName(name);
        var ttl = ttlMinutes ?? DefaultTtlMinutes;
        if (ttl < 1 || ttl > MaxTtlMinutes)
            throw new ArgumentException($"ttl_minutes 는 1~{MaxTtlMinutes}");

        var tokenId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        var file = new TokenFile
        {
            TokenHash = Hash(secret), Name = cleanName,
            CreatedAt = now.ToString("o", CultureInfo.InvariantCulture),
            ExpiresAt = now.AddMinutes(ttl).ToString("o", CultureInfo.InvariantCulture),
        };
        await _gate.WaitAsync(ct);
        try { await StoragePaths.AtomicWriteJsonAsync(paths.EdgeTokenFile(tokenId), file, ct); }
        finally { _gate.Release(); }
        return new EdgeTokenResponse { Token = $"{tokenId}.{secret}", ExpiresAt = file.ExpiresAt };
    }

    /// <summary>
    /// 토큰으로 노드를 등록한다. 틀린 · 이미 쓴 · 만료된 토큰은 모두 null(구분해 알려 주지 않는다 — 토큰 존재 탐색을 막는다).
    /// </summary>
    public async Task<EdgeRegisterResponse?> RegisterAsync(string token, string? name, CancellationToken ct)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !IsHex(parts[0]) || parts[0].Length != 16 || parts[1].Length is < 20 or > 100)
            return null;
        var cleanName = CleanName(name);

        await _gate.WaitAsync(ct);
        try
        {
            var path = paths.EdgeTokenFile(parts[0]);
            var file = await ReadAsync<TokenFile>(path, ct);
            if (file is null || file.UsedAt is not null
                || !SameHash(file.TokenHash, parts[1])
                || clock.GetUtcNow() >= DateTimeOffset.Parse(file.ExpiresAt, CultureInfo.InvariantCulture))
                return null;

            var nodeId = "node_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
            var now = clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture);
            // 노드를 먼저 쓰고 토큰을 소비한다 — 사이에서 멈추면 토큰이 남아(재시도 가능) 비밀을 모르는 고아 노드만 생긴다.
            await StoragePaths.AtomicWriteJsonAsync(paths.EdgeNodeFile(nodeId), new NodeFile
            {
                NodeId = nodeId, Name = cleanName ?? file.Name ?? nodeId, RegisteredAt = now, SecretHash = Hash(secret),
            }, ct);
            await StoragePaths.AtomicWriteJsonAsync(path, file with { UsedAt = now, NodeId = nodeId }, ct);
            return new EdgeRegisterResponse { NodeId = nodeId, Secret = secret };
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Bearer 자격(<c>{node_id}.{secret}</c>)을 확인한다. 틀린 · 모르는 · 회수된 노드는 null. 통과하면 마지막 접속 시각을
    /// (최대 1분에 한 번) 갱신한다.
    /// </summary>
    public async Task<EdgeNode?> AuthenticateAsync(string credential, CancellationToken ct)
    {
        var parts = credential.Split('.', 2);
        if (parts.Length != 2 || !parts[0].StartsWith("node_", StringComparison.Ordinal) || parts[0].Length > 64)
            return null;
        string path;
        try { path = paths.EdgeNodeFile(parts[0]); }
        catch (ArgumentException) { return null; }

        var node = await ReadAsync<NodeFile>(path, ct);
        if (node is null || node.RevokedAt is not null || !SameHash(node.SecretHash, parts[1]))
            return null;

        var now = clock.GetUtcNow();
        if (node.LastSeen is null || now - DateTimeOffset.Parse(node.LastSeen, CultureInfo.InvariantCulture) >= TouchInterval)
        {
            node = node with { LastSeen = now.ToString("o", CultureInfo.InvariantCulture) };
            await _gate.WaitAsync(ct);
            try
            {
                // 잠금 안에서 다시 읽어 그 사이의 회수를 덮어쓰지 않는다.
                var current = await ReadAsync<NodeFile>(path, ct);
                if (current is null || current.RevokedAt is not null)
                    return null;
                await StoragePaths.AtomicWriteJsonAsync(path, current with { LastSeen = node.LastSeen }, ct);
            }
            finally { _gate.Release(); }
        }
        return node.ToNode();
    }

    /// <summary>등록된 노드 전부(등록 순). 비밀은 없다.</summary>
    public async Task<IReadOnlyList<EdgeNode>> ListAsync(CancellationToken ct)
    {
        var dir = paths.EdgeNodesDir();
        if (!Directory.Exists(dir))
            return [];
        var nodes = new List<EdgeNode>();
        foreach (var nodeDir in Directory.EnumerateDirectories(dir))
            if (await ReadAsync<NodeFile>(Path.Combine(nodeDir, "node.json"), ct) is { } node)
                nodes.Add(node.ToNode());
        return [.. nodes.OrderBy(n => n.RegisteredAt, StringComparer.Ordinal).ThenBy(n => n.NodeId, StringComparer.Ordinal)];
    }

    /// <summary>노드를 회수한다 — 이미 회수한 노드는 그대로(멱등). 모르는 노드는 null.</summary>
    /// <exception cref="ArgumentException">node_id 형식 위반.</exception>
    public async Task<EdgeNode?> RevokeAsync(string nodeId, CancellationToken ct)
    {
        var path = paths.EdgeNodeFile(nodeId);
        await _gate.WaitAsync(ct);
        try
        {
            var node = await ReadAsync<NodeFile>(path, ct);
            if (node is null)
                return null;
            if (node.RevokedAt is null)
            {
                node = node with { RevokedAt = clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture) };
                await StoragePaths.AtomicWriteJsonAsync(path, node, ct);
            }
            return node.ToNode();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 노드에 시나리오를 배정한다(전체 교체, 중복 제거 · 정렬). 모르는 노드는 null. 시나리오가 실제로 있는지는 호출부가 확인한다.
    /// </summary>
    /// <exception cref="ArgumentException">시나리오 id 형식 위반 · 회수된 노드.</exception>
    public async Task<EdgeNode?> SetScenariosAsync(string nodeId, IEnumerable<string> scenarioIds, CancellationToken ct)
    {
        var ids = scenarioIds.Select(StoragePaths.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var path = paths.EdgeNodeFile(nodeId);
        await _gate.WaitAsync(ct);
        try
        {
            var node = await ReadAsync<NodeFile>(path, ct);
            if (node is null)
                return null;
            if (node.RevokedAt is not null)
                throw new ArgumentException("회수된 노드에는 시나리오를 배정하지 않는다");
            node = node with { Scenarios = ids };
            await StoragePaths.AtomicWriteJsonAsync(path, node, ct);
            return node.ToNode();
        }
        finally { _gate.Release(); }
    }

    /// <summary>에이전트가 보고한 시계 오프셋을 노드 레코드에 남긴다(원본 사건 시각은 건드리지 않는다). 같은 값이면 쓰지 않는다.</summary>
    public async Task RecordClockOffsetAsync(string nodeId, long offsetMs, CancellationToken ct)
    {
        var path = paths.EdgeNodeFile(nodeId);
        await _gate.WaitAsync(ct);
        try
        {
            var node = await ReadAsync<NodeFile>(path, ct);
            if (node is null || node.RevokedAt is not null || node.ClockOffsetMs == offsetMs)
                return;
            await StoragePaths.AtomicWriteJsonAsync(path, node with { ClockOffsetMs = offsetMs }, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 에이전트의 자기 상태 보고를 노드 레코드에 남긴다(가장 최근 하나만). 같은 값이 <see cref="TouchInterval"/> 안에 다시 오면 디스크에
    /// 쓰지 않는다 — 30 초 폴링마다 노드 파일을 다시 쓰지 않으려는 것이고, 값이 바뀌면 곧바로 쓴다. 회수된 노드 · 모르는 노드는 무시.
    /// </summary>
    /// <exception cref="ArgumentException">값이 범위를 벗어남(음수 · 판 문자열 길이/제어 문자).</exception>
    public async Task RecordStatusAsync(string nodeId, EdgeStatusReport report, CancellationToken ct)
    {
        if (report.OutboxDepth < 0 || report.OutboxBytes < 0 || report.DroppedCrops < 0 || report.Parked < 0)
            throw new ArgumentException("보고의 수는 0 이상이어야 한다");
        var version = report.AgentVersion?.Trim();
        if (version is { Length: > MaxAgentVersionLength } || version?.Any(char.IsControl) == true)
            throw new ArgumentException($"agent_version 은 제어 문자 없는 {MaxAgentVersionLength}자 이하");
        if (string.IsNullOrEmpty(version)) version = null;
        var worker = CleanWorker(report.Worker);

        var path = paths.EdgeNodeFile(nodeId);
        var now = clock.GetUtcNow();
        await _gate.WaitAsync(ct);
        try
        {
            var node = await ReadAsync<NodeFile>(path, ct);
            if (node is null || node.RevokedAt is not null)
                return;
            var previous = node.Status;
            var same = previous is not null
                && previous.OutboxDepth == report.OutboxDepth && previous.OutboxBytes == report.OutboxBytes
                && previous.DroppedCrops == report.DroppedCrops && previous.Parked == report.Parked
                && previous.AgentVersion == version
                && SameWorker(previous.Worker, worker);
            if (same && now - DateTimeOffset.Parse(previous!.ReportedAt, CultureInfo.InvariantCulture) < TouchInterval)
                return;
            await StoragePaths.AtomicWriteJsonAsync(path, node with
            {
                Status = new EdgeNodeStatus
                {
                    ReportedAt = now.ToString("o", CultureInfo.InvariantCulture),
                    OutboxDepth = report.OutboxDepth, OutboxBytes = report.OutboxBytes,
                    DroppedCrops = report.DroppedCrops, Parked = report.Parked, AgentVersion = version, Worker = worker,
                },
            }, ct);
        }
        finally { _gate.Release(); }
    }

    private const int MaxReasonLength = 200;
    private const int MaxSkippedListed = 10;

    /// <summary>워커 수신 상태를 검증하고 다듬는다 — 범위 밖은 <see cref="ArgumentException"/>.</summary>
    private static EdgeWorkerStatus? CleanWorker(EdgeWorkerStatus? worker)
    {
        if (worker is null) return null;
        if (worker.Frames < 0 || worker.Dropped < 0 || worker.Rules < 0 || worker.SkippedRules < 0)
            throw new ArgumentException("worker 의 수는 0 이상이어야 한다");
        string? Text(string? value, string field)
        {
            var t = value?.Trim();
            if (string.IsNullOrEmpty(t)) return null;
            if (t.Length > MaxReasonLength || t.Any(char.IsControl))
                throw new ArgumentException($"{field} 는 제어 문자 없는 {MaxReasonLength}자 이하");
            return t;
        }
        string? lastMessage = null;
        if (worker.LastMessageAt is { } at)
        {
            if (!DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                || !(at.EndsWith('Z') || at.Length > 6 && (at[^6] is '+' or '-')))
                throw new ArgumentException("worker.last_message_at 은 시간대가 있는 ISO 8601 시각");
            lastMessage = parsed.ToString("o", CultureInfo.InvariantCulture);
        }
        if (worker.Skipped is { Count: > MaxSkippedListed })
            throw new ArgumentException($"worker.skipped 는 {MaxSkippedListed}개 이하");
        return worker with
        {
            LastMessageAt = lastMessage,
            LastDropReason = Text(worker.LastDropReason, "worker.last_drop_reason"),
            Skipped = worker.Skipped is { Count: > 0 } list ? [.. list.Select(s => Text(s, "worker.skipped[]") ?? "")] : null,
        };
    }

    // 프레임 수 · 마지막 메시지 시각은 보고마다 달라지므로 «같은 상태» 판정에서 뺀다 — 안 그러면 30 초 폴링마다 노드 파일을 다시 쓴다.
    private static bool SameWorker(EdgeWorkerStatus? a, EdgeWorkerStatus? b) =>
        a is null || b is null
            ? a is null && b is null
            : a.Connected == b.Connected && a.ConfigStale == b.ConfigStale && a.Dropped == b.Dropped && a.LastDropReason == b.LastDropReason
              && a.Rules == b.Rules && a.SkippedRules == b.SkippedRules && (a.Skipped ?? []).SequenceEqual(b.Skipped ?? []);

    private static string? CleanName(string? name)
    {
        if (name is null) return null;
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
            throw new ArgumentException($"name 은 제어 문자 없는 {MaxNameLength}자 이하");
        return trimmed;
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            // 에이전트의 상태 보고 · 구성 폴링이 같은 파일을 계속 교체하고 콘솔 목록이 읽는다 — 교체 순간의 거절은 다시 시도한다.
            await using var stream = await StoragePaths.OpenReadResilientAsync(path, ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, StoragePaths.Json, ct);
        }
        catch (JsonException) { return null; } // 잘린 파일은 없는 것으로 — 인증은 닫힌 쪽으로 실패한다.
        catch (FileNotFoundException) { return null; } // Exists 확인과 열기 사이에 지워졌다(노드 삭제).
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static bool SameHash(string storedHash, string presentedSecret) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(Hash(presentedSecret)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);
}
