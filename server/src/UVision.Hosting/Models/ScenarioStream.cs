using System.Text.Json.Serialization;

namespace UVision.Api.Models;

// 스트림 감시 시나리오(type: monitoring · trigger: stream)의 어휘 — 고정 카메라 영상을 엣지 노드가 보고 «규칙» 으로 사건을 낸다.
// 규칙은 에이전트가 돌리고(워커는 지각만 한다) 구성은 엔진이 정본이다 — dev-docs/U-Vision/edge-worker.md (원장 D-105).
// 모든 필드는 nullable · additive 이고 경계는 지어진 값만 받는다(ScenarioVocabulary).

/// <summary>한 카메라 소스 — 구역(zone)과 선(line)은 이 소스의 프레임 좌표(0~1, 해상도 무관)로 그린다.</summary>
public sealed record ScenarioSource
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>
    /// 카메라 별칭 — 실제 RTSP 주소(비밀번호가 들어갈 수 있다)는 시나리오에 적지 않는다. 노드 안의 로컬 파일 · 환경에서 풀린다(D-105 W-5).
    /// 그래서 URL 모양(<c>:</c> · <c>/</c> · <c>@</c>)은 경계에서 거부한다.
    /// </summary>
    [JsonPropertyName("camera_ref")] public required string CameraRef { get; init; }

    [JsonPropertyName("zones")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ScenarioZone>? Zones { get; init; }

    [JsonPropertyName("lines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ScenarioLine>? Lines { get; init; }
}

/// <summary>구역 — 다각형 꼭짓점 <c>[x, y]</c> 3~64개(0~1, 왼쪽 위 원점 · y 는 아래로). 트랙 상자의 <b>아래 중심</b>(땅에 닿은 점)이 안에 있으면 «구역 안» 이다.</summary>
public sealed record ScenarioZone
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("polygon")] public required List<List<double>> Polygon { get; init; }
}

/// <summary>선 — 두 끝점 <c>[x, y]</c>(0~1). 트랙 상자의 아래 중심이 선분을 가로지를 때, a 에서 b 를 화면에서 바라보아 <b>오른쪽</b>으로 넘어가면 <c>in</c>, 왼쪽으로 넘어가면 <c>out</c> 이다.</summary>
public sealed record ScenarioLine
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("a")] public required List<double> A { get; init; }

    [JsonPropertyName("b")] public required List<double> B { get; init; }
}

/// <summary>
/// 규칙 하나. <c>count</c>: 구역(없으면 프레임 전체) 안 <c>class</c> 개수가 <c>stable_frames</c>(기본 5) 또는 <c>stable_seconds</c> 동안
/// 안정일 때만 변화 사건(<c>count_changed</c>) — 선(<c>line</c>)을 주면 구역 대신 통과 in/out 을 센다. <c>alarm</c>: 개수 조건
/// (<c>min_count</c> 이상 또는 <c>max_count</c> 이하 — 존재 = min 1, 부재 = max 0)이 <c>for_seconds</c> 이상 지속하면 <c>alarm_raised</c>,
/// 반대 상태가 <c>clear_seconds</c>(기본 <c>for_seconds</c>) 지속하면 <c>alarm_cleared</c>.
/// </summary>
public sealed record ScenarioRule
{
    [JsonPropertyName("id")] public required string Id { get; init; }

    [JsonPropertyName("kind")] public required string Kind { get; init; }

    [JsonPropertyName("source")] public required string Source { get; init; }

    [JsonPropertyName("zone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Zone { get; init; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Line { get; init; }

    /// <summary>세거나 지켜볼 물건의 종류 — 워커의 검출기가 내는 라벨.</summary>
    [JsonPropertyName("class")] public required string Class { get; init; }

    [JsonPropertyName("stable_frames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StableFrames { get; init; }

    [JsonPropertyName("stable_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StableSeconds { get; init; }

    [JsonPropertyName("min_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinCount { get; init; }

    [JsonPropertyName("max_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxCount { get; init; }

    [JsonPropertyName("for_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ForSeconds { get; init; }

    [JsonPropertyName("clear_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ClearSeconds { get; init; }
}

/// <summary>
/// 내보낼 사건 — 생략하면 규칙이 낼 수 있는 모든 사건을 엔진으로 보낸다. 있으면 여기 적힌 것만 보낸다(<c>rule</c> 을 주면 그 규칙의 것만).
/// 사건을 받는 곳(웹훅 · MQTT · OPC UA)은 시나리오가 아니라 엔진의 사건 내보내기 설정이 정한다.
/// </summary>
public sealed record ScenarioOutput
{
    [JsonPropertyName("event")] public required string Event { get; init; }

    [JsonPropertyName("rule")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rule { get; init; }
}
