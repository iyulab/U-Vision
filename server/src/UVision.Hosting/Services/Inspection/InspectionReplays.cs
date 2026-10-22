using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using UVision.Api.Configuration;
using UVision.Api.Storage;

namespace UVision.Api.Services.Inspection;

/// <summary>
/// 검사 요청의 멱등 키(<c>Idempotency-Key</c> 헤더, IETF httpapi-idempotency-key-header) — 요청은 닿았는데 응답을
/// 잃은 장치가 같은 캡처를 다시 보내도 판정 · 레코드 · 결정 장부 · 판정 사건이 한 번만 생기게 한다.
/// <para>
/// 같은 키가 다시 오면 판정을 다시 돌리지 않고 처음 응답 본문을 byte 그대로 돌려준다(<c>Idempotent-Replayed: true</c>).
/// 같은 키에 다른 요청(이미지 · 시나리오 · 맥락이 다름)이면 422. 같은 키가 처리 중이면 끝나기를 기다렸다가 그 결과를
/// 돌려준다. 저장하는 것은 성공(200) 응답뿐이다 — 판정 불가(503) · 기록 실패(500)는 다시 보내면 다시 판정한다(그 경우
/// 결정 장부에 판정 불가 줄이 하나 더 생긴다 — 장부는 관찰 기록이다).
/// </para>
/// <para>재전송 응답 저장은 degrade — 판정은 이미 기록됐으므로 실패해도 응답은 그대로 나가고, 그 키의 재전송만 다시 판정된다.</para>
/// </summary>
public sealed class InspectionReplays(
    StoragePaths paths, StorageOptions storage, IOptions<JsonOptions> jsonOptions, ILogger<InspectionReplays> logger)
{
    public const string KeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    private const string JsonContentType = "application/json; charset=utf-8";
    private static readonly JsonSerializerOptions FingerprintJson = new();

    // 처리 중인 키 — 같은 키의 두 번째 요청은 첫 요청이 끝나기를 기다린 뒤 저장된 응답을 본다(프로세스 안 전제).
    private readonly ConcurrentDictionary<string, Task> _inflight = new();

    /// <summary>키는 파일 이름이 되므로 저장소 식별자 규칙(<c>[A-Za-z0-9._-]</c>, 128자)을 따른다.</summary>
    public static bool IsValidKey(string key)
    {
        try
        {
            StoragePaths.Id(key);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>요청의 지문 — 같은 키로 온 요청이 처음 요청과 같은지 가른다.</summary>
    public static string Fingerprint(string operation, ReadOnlySpan<byte> image, params string?[] fields)
    {
        var parts = new List<string?>(fields.Length + 2) { operation, Convert.ToHexStringLower(SHA256.HashData(image)) };
        parts.AddRange(fields);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(parts, FingerprintJson)));
    }

    /// <summary>
    /// 키가 처음이면 <paramref name="run"/> 으로 판정하고 그 성공 응답(<c>Replayable</c> 이 null 이 아닌 것)을 저장한다.
    /// 저장된 키면 그 응답을 돌려준다.
    /// </summary>
    public async Task<IResult> RunAsync(
        string scenarioId, string key, string fingerprint,
        Func<Task<(IResult Result, object? Replayable)>> run, CancellationToken cancellationToken)
    {
        var slot = scenarioId + "/" + key;
        while (true)
        {
            var stored = await ReadAsync(scenarioId, key, cancellationToken);
            if (stored is not null)
            {
                if (stored.Fingerprint != fingerprint)
                    return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                        detail: $"{KeyHeader} 가 이미 다른 요청에 쓰였다");
                logger.LogInformation("재전송 — 저장된 응답을 돌려준다 (scenario={ScenarioId}, key={Key})", scenarioId, key);
                return new JsonBody(Encoding.UTF8.GetBytes(stored.Body), replayed: true);
            }

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inflight.TryAdd(slot, gate.Task))
            {
                try
                {
                    var (result, replayable) = await run();
                    if (replayable is null)
                        return result;
                    var body = JsonSerializer.SerializeToUtf8Bytes(
                        replayable, replayable.GetType(), jsonOptions.Value.SerializerOptions);
                    await SaveSafelyAsync(scenarioId, key, fingerprint, body, cancellationToken);
                    return new JsonBody(body, replayed: false);
                }
                finally
                {
                    _inflight.TryRemove(slot, out _);
                    gate.SetResult();
                }
            }

            if (_inflight.TryGetValue(slot, out var running))
                await running.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 보존 기한(<see cref="StorageOptions.IdempotencyRetentionHours"/>)이 지난 재전송 응답을 지운다. 지운 수를 돌려준다.
    /// 나이는 파일을 쓴 시각(atomic write — 처음 응답 시각)으로 잰다.
    /// </summary>
    public int PruneExpired(DateTime nowUtc)
    {
        if (storage.IdempotencyRetentionHours <= 0)
            return 0;
        var cutoff = nowUtc.AddHours(-storage.IdempotencyRetentionHours);
        var removed = 0;
        foreach (var scenarioId in paths.ScenarioIds())
        {
            string dir;
            try { dir = paths.ReplayDir(scenarioId); }
            catch (ArgumentException) { continue; } // 시나리오가 아닌 디렉터리
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    continue;
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "만료된 재전송 응답 삭제 실패 — 다음 주기 재시도 ({File})", file);
                }
            }
        }
        return removed;
    }

    private async Task<StoredReplay?> ReadAsync(string scenarioId, string key, CancellationToken cancellationToken)
    {
        var path = paths.ReplayFile(scenarioId, key);
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredReplay>(stream, StoragePaths.Json, cancellationToken);
    }

    private async Task SaveSafelyAsync(
        string scenarioId, string key, string fingerprint, byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            await StoragePaths.AtomicWriteJsonAsync(paths.ReplayFile(scenarioId, key), new StoredReplay
            {
                Fingerprint = fingerprint,
                CreatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Body = Encoding.UTF8.GetString(body),
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "재전송 응답 저장 실패 — 판정은 그대로, 이 키의 재전송은 다시 판정된다 (scenario={ScenarioId}, key={Key})",
                scenarioId, key);
        }
    }

    /// <summary>재전송 응답 파일 — 본문은 처음 응답 그대로(문자열로 담아 들여쓰기가 본문을 바꾸지 않게).</summary>
    private sealed class StoredReplay
    {
        [JsonPropertyName("fingerprint")]
        public required string Fingerprint { get; init; }

        [JsonPropertyName("created_at")]
        public required string CreatedAt { get; init; }

        [JsonPropertyName("body")]
        public required string Body { get; init; }
    }

    /// <summary>JSON 본문을 byte 그대로 쓴다 — 처음 응답과 재전송 응답이 같은 byte 가 되게.</summary>
    private sealed class JsonBody(byte[] body, bool replayed) : IResult, IStatusCodeHttpResult
    {
        public int? StatusCode => StatusCodes.Status200OK;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = JsonContentType;
            response.ContentLength = body.Length;
            if (replayed)
                response.Headers[ReplayedHeader] = "true";
            return response.Body.WriteAsync(body).AsTask();
        }
    }
}

/// <summary>만료된 재전송 응답을 시작할 때와 한 시간마다 지운다.</summary>
public sealed class InspectionReplayExpiryService(
    InspectionReplays replays, StorageOptions storage, ILogger<InspectionReplayExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (storage.IdempotencyRetentionHours <= 0)
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = replays.PruneExpired(DateTime.UtcNow);
                if (removed > 0)
                    logger.LogInformation("만료된 재전송 응답 {Count}개 삭제", removed);
            }
            catch (Exception ex) { logger.LogWarning(ex, "재전송 응답 만료 정리 실패 — 다음 주기 재시도"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
