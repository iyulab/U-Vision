using Opc.Ua;
using Opc.Ua.Server;
using UVision.Api.Services.Events;
using UVision.Api.Storage;

namespace UVision.Api.OpcUa;

/// <summary>
/// OPC UA — 판정 사건을 최근 결과 창에 넣고 결과 준비 사건을 올린다. 웹훅 · MQTT 와 달리 구독자 확인(ack)이 없다:
/// «서버가 받았다» 가 곧 전달이라, 구독자가 없어도 성공이고 놓친 구독자는 조회 메서드로 따라잡는다. 실패는 서버가 서 있지 않거나
/// 본문을 못 읽을 때뿐이다(던지지 않고 오류 문구를 돌려줘 outbox 의 재시도 · 포기에 맡긴다).
/// </summary>
public sealed class OpcUaTransport(OpcUaResultHub hub, ScenarioFactsReader scenarios) : IEventTransport
{
    public Task<string?> SendAsync(string scenarioId, OutboxEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!hub.IsRunning)
            return Task.FromResult<string?>("OPC UA server not running");
        // 엣지 노드의 규칙 사건은 «결과 관리» 에 싣지 않는다 — OPC 40100-1 의 모양은 판정 결과이고, 이 종류는 웹훅 · MQTT 로만 나간다.
        // 실패가 아니라 «이 받는 곳이 다루지 않는 종류» 라서 전달된 것으로 센다(재시도 · 포기 대상이 아니다).
        if (ResultMapper.IsEdgeEvent(entry.Body))
            return Task.FromResult<string?>(null);
        if (!ResultMapper.TryMap(entry.Body, scenarios.Read(scenarioId), out var result, out var error))
            return Task.FromResult<string?>(error);
        return Task.FromResult(hub.Publish(result!));
    }
}

/// <summary>시나리오 정의(<c>scenario.json</c>)에서 매핑에 필요한 사실을 읽는다 — 파일이 없거나 못 읽으면 <see cref="ScenarioFacts.Unknown"/>.</summary>
public sealed class ScenarioFactsReader(StoragePaths paths)
{
    public ScenarioFacts Read(string scenarioId)
    {
        try
        {
            var file = paths.ScenarioJson(scenarioId);
            if (!File.Exists(file)) return ScenarioFacts.Unknown;
            using var reader = new StreamReader(StoragePaths.OpenReadResilient(file));
            return ScenarioFacts.FromScenarioJson(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ScenarioFacts.Unknown;
        }
    }
}

/// <summary>OPC UA 를 사건의 받는 곳으로 더한다 — 엔진 호스트가 OPC UA 를 켰을 때만 DI 에 등록된다.</summary>
public sealed class OpcUaDestinationSource : IEventDestinationSource
{
    /// <summary>받는 곳 이름 — outbox 디렉터리 이름이라 설정된 웹훅 · MQTT 이름과 겹치면 기동에서 멈춘다.</summary>
    public const string DestinationName = "opcua";

    public const string Kind = EventDestination.OpcUaKind;

    public OpcUaDestinationSource(OpcUaResultHub hub, ScenarioFactsReader scenarios)
    {
        Destinations = [new EventDestination(DestinationName, Kind, new OpcUaTransport(hub, scenarios))];
    }

    public IReadOnlyList<EventDestination> Destinations { get; }
}

/// <summary>결과 노드 관리자를 서버 스택에 건네는 공장 — 노드 관리자는 서버가 지만 보관소(<see cref="OpcUaResultHub"/>)는 DI 가 쥔다.</summary>
public sealed class ResultsNodeManagerFactory(OpcUaResultHub hub) : IAsyncNodeManagerFactory
{
    public ArrayOf<string> NamespacesUris => new[] { OpcUaResultNames.NamespaceUri };

    public ValueTask<IAsyncNodeManager> CreateAsync(
        IServerInternal server, ApplicationConfiguration configuration, CancellationToken cancellationToken = default) =>
        // 소유권은 마스터 노드 관리자로 넘어가 서버가 내려갈 때 해제된다.
        ValueTask.FromResult<IAsyncNodeManager>(new ResultsNodeManager(server, configuration, hub));
}
