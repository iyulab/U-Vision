using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using UVision.Edge.Agent.Worker;

namespace UVision.Edge.Agent;

// --- 엔진과 주고받는 wire (docs/api.md «엣지 노드 신원»). 엔진 어셈블리를 참조하지 않는다 — 에이전트는 별개 이미지다. ---

public sealed record RegisterResult(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("secret")] string Secret);

public sealed record EdgeScenarioConfig
{
    [JsonPropertyName("scenario_id")] public required string ScenarioId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("trigger")] public string? Trigger { get; init; }
    [JsonPropertyName("sources")] public List<StreamSource>? Sources { get; init; }
    [JsonPropertyName("rules")] public List<StreamRule>? Rules { get; init; }
    [JsonPropertyName("outputs")] public List<StreamOutput>? Outputs { get; init; }
    [JsonPropertyName("expected_count")] public int? ExpectedCount { get; init; }
    [JsonPropertyName("count_tolerance")] public int? CountTolerance { get; init; }
    [JsonPropertyName("min_confidence")] public double? MinConfidence { get; init; }
    [JsonPropertyName("count_classes")] public List<string>? CountClasses { get; init; }
}

public sealed record EdgeConfig
{
    [JsonPropertyName("node_id")] public required string NodeId { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("scenarios")] public List<EdgeScenarioConfig> Scenarios { get; init; } = [];
    [JsonPropertyName("missing_scenarios")] public List<string>? MissingScenarios { get; init; }
}

public sealed record SeqRange(
    [property: JsonPropertyName("from")] long From,
    [property: JsonPropertyName("to")] long To);

public sealed record EventResult
{
    [JsonPropertyName("event_id")] public string EventId { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("code")] public string? Code { get; init; }
}

public sealed record EventsResponse
{
    [JsonPropertyName("results")] public List<EventResult> Results { get; init; } = [];
    [JsonPropertyName("missing_seq")] public List<SeqRange>? MissingSeq { get; init; }
}

/// <summary>설정 폴링의 결과 — 바뀌지 않았으면 <see cref="Config"/> 가 null(<c>304</c>). <see cref="ServerDate"/> 는 응답의 <c>Date</c>.</summary>
public sealed record ConfigPoll(EdgeConfig? Config, DateTimeOffset? ServerDate, DateTimeOffset SentAt, DateTimeOffset ReceivedAt);

/// <summary>엔진이 이 노드의 자격을 받아들이지 않는다(회수됐거나 틀림) — 에이전트는 멈추고 사람이 새로 등록해야 한다.</summary>
public sealed class CredentialsRejectedException() : Exception("엔진이 노드 자격을 거절했다(회수됐거나 맞지 않음)");

/// <summary>엔진이 요청을 영구히 받지 않는다(400 …) — 다시 보내도 같다.</summary>
public sealed class PermanentRejectionException(HttpStatusCode status, string detail)
    : Exception($"엔진이 요청을 거절했다 ({(int)status}): {detail}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// 엔진으로 <b>나가는 방향</b>의 HTTPS 클라이언트 — 등록 · 구성 폴링(<c>ETag</c>) · 사건 배치 · 크롭 판정. 네트워크 실패는
/// <see cref="HttpRequestException"/> · <see cref="TaskCanceledException"/> 그대로 올려 호출부(에이전트)가 백오프하게 한다.
/// </summary>
public sealed class EngineClient(HttpClient http, string apiBase, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly string _base = apiBase.TrimEnd('/');
    private static readonly JsonSerializerOptions Json = new();

    public async Task<RegisterResult> RegisterAsync(string token, string? name, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync($"{_base}/edge/v1/register", new { token, name }, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new CredentialsRejectedException();
        await EnsureAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<RegisterResult>(Json, ct))!;
    }

    public async Task<ConfigPoll> GetConfigAsync(string credential, string? etagVersion, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Get, "/edge/v1/config", credential);
        if (etagVersion is not null) req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{etagVersion}\""));
        var sent = _clock.GetUtcNow();
        using var resp = await http.SendAsync(req, ct);
        var received = _clock.GetUtcNow();
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new CredentialsRejectedException();
        if (resp.StatusCode == HttpStatusCode.NotModified) return new ConfigPoll(null, resp.Headers.Date, sent, received);
        await EnsureAsync(resp, ct);
        return new ConfigPoll(await resp.Content.ReadFromJsonAsync<EdgeConfig>(Json, ct), resp.Headers.Date, sent, received);
    }

    public async Task<EventsResponse> PostEventsAsync(
        string credential, IReadOnlyList<OutboxItem> events, long? clockOffsetMs, CancellationToken ct)
    {
        using var req = Authorized(HttpMethod.Post, "/edge/v1/events", credential);
        req.Content = JsonContent.Create(new
        {
            events = events.Select(e => new
            {
                event_id = e.Id, seq = e.Seq, scenario_id = e.ScenarioId, kind = e.EventKind, device_time = e.DeviceTime, data = e.Data,
            }),
            clock_offset_ms = clockOffsetMs,
        });
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new CredentialsRejectedException();
        await EnsureAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<EventsResponse>(Json, ct))!;
    }

    /// <summary>
    /// 자기 상태(outbox 깊이 · 버린 크롭 …)를 엔진에 알린다 — 운영자의 노드 목록용이다. 엔진이 이 경로를 모르면(옛 판)
    /// <see cref="PermanentRejectionException"/> 이다.
    /// </summary>
    public async Task PostStatusAsync(
        string credential, OutboxStats stats, string? agentVersion, CancellationToken ct, WorkerStatusReport? worker = null)
    {
        using var req = Authorized(HttpMethod.Post, "/edge/v1/status", credential);
        req.Content = JsonContent.Create(new
        {
            outbox_depth = stats.Depth, outbox_bytes = stats.Bytes, dropped_crops = stats.DroppedCrops, parked = stats.Parked,
            agent_version = agentVersion, worker,
        });
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new CredentialsRejectedException();
        await EnsureAsync(resp, ct);
    }

    /// <summary>
    /// 판정이 필요한 크롭을 엔진의 <c>/v2/inspect</c> 로 보낸다. <c>Idempotency-Key</c> = 항목 id 라 응답을 잃고 다시 보내도 판정은
    /// 한 번이다. 성공(200)과 «판정 불가(503)» 는 엔진이 답을 정한 것이라 둘 다 전달로 센다(503 은 fail-closed 가 정한 결과다).
    /// </summary>
    public async Task SendCropAsync(OutboxItem item, byte[] image, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(item.ScenarioId), "scenario_id" },
            { new StringContent(item.DeviceTime), "captured_at" },
        };
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue(item.ContentType ?? "image/jpeg");
        form.Add(file, "image", item.CropFile ?? "crop.jpg");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/v2/inspect") { Content = form };
        req.Headers.Add("Idempotency-Key", item.Id);
        using var resp = await http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.ServiceUnavailable && resp.Content.Headers.ContentType?.MediaType == "application/json")
            return;
        await EnsureAsync(resp, ct);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path, string credential)
    {
        var req = new HttpRequestMessage(method, _base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return req;
    }

    /// <summary>
    /// 4xx(401 은 따로 처리) 는 영구 거절, 5xx · 408 · 429 는 일시 실패(<see cref="HttpRequestException"/>)로 다룬다 — 다시 보내면 될 수 있다.
    /// </summary>
    private static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var code = (int)resp.StatusCode;
        var detail = await resp.Content.ReadAsStringAsync(ct);
        if (code >= 500 || code is 408 or 429) throw new HttpRequestException($"엔진 일시 오류 ({code})", null, resp.StatusCode);
        throw new PermanentRejectionException(resp.StatusCode, detail.Length > 300 ? detail[..300] : detail);
    }
}

/// <summary>
/// 서버 시계와의 오프셋 추정 — 구성 폴링 응답의 <c>Date</c> 로 «서버 시각 − 노드 시각» 을 잰다(원장 D-102 E-5).
/// <c>Date</c> 는 초 단위로 잘린 값이라(+[0, 1) 초의 불확실성) 중앙값 0.5 초를 더해 쓰고, 왕복의 가운데를 노드 시각으로 삼는다.
/// 한 번의 표본은 흔들리므로 최근 표본의 <b>중앙값</b>을 쓴다. 정확도는 대략 ±0.5 초 + 왕복 지연의 반 — 규칙 사건의 시각 보정에는 충분하고
/// 초 이하 정밀도가 필요한 용도에는 쓰지 않는다(edge.md 의 가정, 실측은 하니스에서).
/// </summary>
public sealed class ClockOffsetEstimator(int window = 9)
{
    private readonly Queue<long> _samples = new();

    /// <summary>표본이 하나라도 있는가.</summary>
    public bool HasEstimate => _samples.Count > 0;

    public void Observe(DateTimeOffset sentAt, DateTimeOffset receivedAt, DateTimeOffset serverDate)
    {
        var midpoint = sentAt + (receivedAt - sentAt) / 2;
        var serverEstimate = serverDate + TimeSpan.FromMilliseconds(500); // Date 는 초로 잘린 값 — 구간 [0, 1) 초의 가운데
        _samples.Enqueue((long)(serverEstimate - midpoint).TotalMilliseconds);
        while (_samples.Count > window) _samples.Dequeue();
    }

    /// <summary>최근 표본의 중앙값(밀리초). 표본이 없으면 null.</summary>
    public long? OffsetMs()
    {
        if (_samples.Count == 0) return null;
        var sorted = _samples.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
