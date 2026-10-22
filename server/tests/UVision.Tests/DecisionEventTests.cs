using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using UVision.Api.Configuration;
using UVision.Api.Models;
using UVision.Api.Services.Events;
using UVision.Api.Storage;
using Xunit;

namespace UVision.Tests;

/// <summary>판정 사건 내보내기(P4 웹훅) — 서명 · outbox · 보내기 · 파이프라인 결선.</summary>
public class DecisionEventTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uvision-events-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private StoragePaths Paths => new(new StorageOptions { DataPath = _root }, AppContext.BaseDirectory);

    private static EventsOptions Options(int maxAttempts = 15, string secret = "") => new()
    {
        MaxAttempts = maxAttempts,
        Webhooks = [new WebhookOptions { Name = "mes", Url = "http://receiver.test/hook", Secret = secret }],
    };

    private static InspectResultV2 Decision(string id) => new()
    {
        DecisionId = id,
        ScenarioId = "demo",
        Timestamp = "2026-10-22T12:00:00.0000000Z",
        Class = "OK",
    };

    [Fact]
    public void Signature_Matches_Standard_Webhooks_Reference_Vector()
    {
        // standard-webhooks 참조 구현의 테스트 벡터.
        var secret = WebhookSignature.SecretBytes("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw");
        var sig = WebhookSignature.Sign(secret, "msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, "{\"test\": 2432232314}");
        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", sig);
    }

    [Fact]
    public async Task Publisher_Writes_CloudEvent_Per_Webhook_And_Nothing_When_Disabled()
    {
        await new DecisionEventPublisher(Paths, new EventsOptions(), new WebhookDeliverySignal())
            .PublishAsync(Decision("img_a"), CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(_root, "demo", "events")));

        await new DecisionEventPublisher(Paths, Options(), new WebhookDeliverySignal())
            .PublishAsync(Decision("img_b"), CancellationToken.None);
        var file = Assert.Single(Directory.GetFiles(Paths.EventOutboxDir("demo", "mes"), "*.json"));
        var entry = JsonSerializer.Deserialize<OutboxEntry>(await File.ReadAllTextAsync(file))!;
        using var evt = JsonDocument.Parse(entry.Body);
        Assert.Equal("1.0", evt.RootElement.GetProperty("specversion").GetString());
        Assert.Equal("img_b", evt.RootElement.GetProperty("id").GetString());
        Assert.Equal("u-vision.inspection.decided", evt.RootElement.GetProperty("type").GetString());
        Assert.Equal("img_b", evt.RootElement.GetProperty("data").GetProperty("decision_id").GetString());
    }

    [Fact]
    public async Task Dispatcher_Sends_Signed_Body_And_Deletes_On_Success()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var options = Options(secret: "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw");
        await new DecisionEventPublisher(Paths, options, new WebhookDeliverySignal())
            .PublishAsync(Decision("img_a"), CancellationToken.None);

        var sent = await Dispatcher(options, handler).DeliverDueAsync(_now, CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Empty(Directory.GetFiles(Paths.EventOutboxDir("demo", "mes"), "*.json"));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("img_a", request.Headers[WebhookSignature.IdHeader]);
        var expected = WebhookSignature.Sign(
            WebhookSignature.SecretBytes(options.Webhooks[0].Secret), "img_a", _now.ToUnixTimeSeconds(), request.Body);
        Assert.Equal(expected, request.Headers[WebhookSignature.SignatureHeader]);
    }

    [Fact]
    public async Task Dispatcher_Backs_Off_Keeps_Order_And_Gives_Up_After_Max_Attempts()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var options = Options(maxAttempts: 2);
        var publisher = new DecisionEventPublisher(Paths, options, new WebhookDeliverySignal());
        await publisher.PublishAsync(Decision("img_a"), CancellationToken.None);
        await publisher.PublishAsync(Decision("img_b"), CancellationToken.None);
        var dispatcher = Dispatcher(options, handler);

        // 첫 실패 — 첫 사건만 시도하고 뒤 사건은 기다린다, 10초 뒤로 미룬다.
        Assert.Equal(0, await dispatcher.DeliverDueAsync(_now, CancellationToken.None));
        Assert.Single(handler.Requests);
        // 쉬는 중엔 보내지 않는다.
        await dispatcher.DeliverDueAsync(_now.AddSeconds(5), CancellationToken.None);
        Assert.Single(handler.Requests);
        // 두 번째 실패 = 상한 → dead/ 로.
        await dispatcher.DeliverDueAsync(_now.AddSeconds(11), CancellationToken.None);
        Assert.Single(Directory.GetFiles(Paths.EventDeadDir("demo", "mes"), "*.json"));

        handler.Status = HttpStatusCode.OK;
        Assert.Equal(1, await dispatcher.DeliverDueAsync(_now.AddSeconds(12), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Paths.EventOutboxDir("demo", "mes"), "*.json"));
    }

    [Fact]
    public async Task Inspect_Leaves_Event_In_Outbox_When_A_Webhook_Is_Configured()
    {
        using var factory = new UVisionApiFactory();
        using var withHook = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("UVision:Events:Webhooks:0:Name", "mes");
            // 닿지 않는 주소 — 보내기는 실패해 outbox 에 남는다.
            b.UseSetting("UVision:Events:Webhooks:0:Url", "http://127.0.0.1:9/hook");
        });
        var client = withHook.CreateClient();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");

        var resp = await client.PostAsync("/api/u-vision/inspect", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var imageId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement
            .GetProperty("image_id").GetString()!;

        var outbox = Path.Combine(factory.DataPath, "demo", "events", "mes");
        var entries = Directory.GetFiles(outbox, "*.json", SearchOption.AllDirectories)
            .Select(f => JsonSerializer.Deserialize<OutboxEntry>(File.ReadAllText(f))!);
        Assert.Contains(entries, e => e.EventId == imageId);
    }

    [Fact]
    public async Task Inspect_Without_Webhooks_Writes_No_Events()
    {
        using var factory = new UVisionApiFactory();
        var client = factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "capture.jpg");
        form.Add(new StringContent("demo"), "scenario_id");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/u-vision/inspect", form)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(factory.DataPath, "demo", "events")));
    }

    private WebhookDispatcher Dispatcher(EventsOptions options, HttpMessageHandler handler) =>
        new(Paths, options, new StubFactory(handler), NullLogger<WebhookDispatcher>.Instance);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public List<(Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            Requests.Add((headers, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Status);
        }
    }
}
