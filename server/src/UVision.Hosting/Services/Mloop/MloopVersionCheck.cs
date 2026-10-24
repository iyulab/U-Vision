using System.Text.Json;
using Microsoft.Extensions.Hosting;
using UVision.Api.Configuration;

namespace UVision.Api.Services.Mloop;

/// <summary>
/// <c>mloop serve</c> 의 <c>GET /health</c> 응답이 U-Vision 이 요구하는 판(<see cref="Minimum"/>)을 넘는지 읽는 순수 판정.
/// 분류기 · 임베더는 이미지 내용을 보내는데, 그보다 낮은 판은 매 판정을 400 으로 거절한다 — ML · 기억은 조용히
/// degrade 되므로 원인이 판이라는 것을 기동 때 한 번 분명히 말한다.
/// </summary>
public static class MloopVersion
{
    /// <summary>이미지 내용 입력(<c>/predict</c>) · 알파 처리가 맞는 <c>/embed</c> 가 들어온 판.</summary>
    public static readonly Version Minimum = new(0, 45, 0);

    public enum Status { Supported, TooOld, Unknown }

    /// <summary><c>/health</c> 본문의 <c>version</c>(예: <c>0.45.0+7ec23be</c>) — 빌드 꼬리표는 버린다.</summary>
    public static (Status Status, string? Reported) Judge(string? healthJson)
    {
        string? reported = null;
        try
        {
            if (healthJson is not null)
            {
                using var doc = JsonDocument.Parse(healthJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    reported = v.GetString();
            }
        }
        catch (JsonException) { }

        var core = reported?.Split('+', '-')[0];
        if (core is null || !Version.TryParse(core, out var parsed))
            return (Status.Unknown, reported);
        return (parsed >= Minimum ? Status.Supported : Status.TooOld, reported);
    }
}

/// <summary>
/// 기동 뒤 한 번, <c>mloop</c> provider 로 켜진 ML 분류기 · 기억 단의 <c>mloop serve</c> 판을 확인해 로그로 알린다.
/// 판정 경로는 건드리지 않고 기동도 막지 않는다 — MLoop 이 U-Vision 보다 늦게 뜰 수 있다.
/// </summary>
public sealed class MloopVersionCheckService(
    MlOptions ml, MemoryOptions memory, IHttpClientFactory httpFactory, ILogger<MloopVersionCheckService> log)
    : BackgroundService
{
    public const string HttpClientName = "uvision-mloop-health";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoints = new List<(string Uses, string Endpoint)>();
        if (ml.Provider.Equals("mloop", StringComparison.OrdinalIgnoreCase))
            endpoints.Add(("ML 분류기", ml.Endpoint));
        if (memory.Provider.Equals("mloop", StringComparison.OrdinalIgnoreCase))
            endpoints.Add(("기억 단", memory.Endpoint));

        foreach (var group in endpoints.GroupBy(e => e.Endpoint.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
        {
            var uses = string.Join(" · ", group.Select(g => g.Uses));
            string? body = null;
            try
            {
                using var http = httpFactory.CreateClient(HttpClientName);
                body = await http.GetStringAsync($"{group.Key}/health", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogInformation("mloop serve({Endpoint}, {Uses}) 판을 확인하지 못했다 — {Reason}. U-Vision 은 {Minimum} 이상이 필요하다",
                    group.Key, uses, ex.Message, MloopVersion.Minimum);
                continue;
            }

            var (status, reported) = MloopVersion.Judge(body);
            switch (status)
            {
                case MloopVersion.Status.Supported:
                    log.LogInformation("mloop serve({Endpoint}, {Uses}) 판 {Version}", group.Key, uses, reported);
                    break;
                case MloopVersion.Status.TooOld:
                    log.LogWarning(
                        "mloop serve({Endpoint}, {Uses}) 판 {Version} — U-Vision 은 {Minimum} 이상이 필요하다. 이 판은 이미지 내용 입력을 " +
                        "거절하므로 위 기능은 판정마다 실패해 빠지고 VLM 만으로 판정된다. mloop 을 올린다(dotnet tool update -g mloop)",
                        group.Key, uses, reported, MloopVersion.Minimum);
                    break;
                default:
                    log.LogInformation("mloop serve({Endpoint}, {Uses}) 가 판을 알리지 않았다(응답 version: {Version}). U-Vision 은 {Minimum} 이상이 필요하다",
                        group.Key, uses, reported ?? "없음", MloopVersion.Minimum);
                    break;
            }
        }
    }
}
