using System.Text.Json.Serialization;

namespace UVision.Edge.Agent;

// 스트림 감시 시나리오(type: monitoring)의 구성 wire — 엔진의 ScenarioStream.cs 와 같은 모양이다. 에이전트는 엔진 어셈블리를
// 참조하지 않으므로(별개 이미지) 따로 둔다. 어휘 정의와 뜻은 dev-docs/U-Vision/edge-worker.md (원장 D-105).

/// <summary>카메라 소스. 좌표는 프레임 기준 0~1(해상도 무관). <see cref="CameraRef"/> 는 노드 로컬 별칭이다 — 실제 주소는 노드 안에서만 풀린다.</summary>
public sealed record StreamSource
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("camera_ref")] public string CameraRef { get; init; } = "";
    [JsonPropertyName("zones")] public List<StreamZone>? Zones { get; init; }
    [JsonPropertyName("lines")] public List<StreamLine>? Lines { get; init; }
}

public sealed record StreamZone
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>꼭짓점 <c>[x, y]</c> 3개 이상.</summary>
    [JsonPropertyName("polygon")] public List<List<double>> Polygon { get; init; } = [];
}

public sealed record StreamLine
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("a")] public List<double> A { get; init; } = [];
    [JsonPropertyName("b")] public List<double> B { get; init; } = [];
}

/// <summary>규칙 하나 — <c>count</c> 또는 <c>alarm</c>. 필드 뜻은 엔진의 <c>ScenarioRule</c> 설명과 같다.</summary>
public sealed record StreamRule
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("zone")] public string? Zone { get; init; }
    [JsonPropertyName("line")] public string? Line { get; init; }
    [JsonPropertyName("class")] public string Class { get; init; } = "";
    [JsonPropertyName("stable_frames")] public int? StableFrames { get; init; }
    [JsonPropertyName("stable_seconds")] public double? StableSeconds { get; init; }
    [JsonPropertyName("min_count")] public int? MinCount { get; init; }
    [JsonPropertyName("max_count")] public int? MaxCount { get; init; }
    [JsonPropertyName("for_seconds")] public double? ForSeconds { get; init; }
    [JsonPropertyName("clear_seconds")] public double? ClearSeconds { get; init; }
}

public sealed record StreamOutput
{
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("rule")] public string? Rule { get; init; }
}
