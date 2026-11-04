using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Services.Edge;

/// <summary>
/// 엣지 구성 만들기(D-102 E-2 · E-3) — 노드에 배정된 시나리오를 읽어 엣지용으로 <b>투영</b>하고, 그 내용의 지문을 <c>version</c> 으로 준다.
/// 지문은 투영한 값과 배정만 덮는다: VLM 프롬프트처럼 엣지가 쓰지 않는 값이 바뀌어도 노드를 깨우지 않는다. 읽기 전용이다.
/// </summary>
public sealed class EdgeConfigBuilder(IScenarioStore scenarios)
{
    private static readonly JsonSerializerOptions Canonical = new(StoragePaths.Json) { WriteIndented = false };

    public async Task<EdgeConfig> BuildAsync(EdgeNode node, CancellationToken ct)
    {
        var projected = new List<EdgeScenarioConfig>();
        var missing = new List<string>();
        foreach (var id in node.Scenarios.Order(StringComparer.Ordinal))
        {
            if (await scenarios.GetAsync(id, ct) is { } scenario)
                projected.Add(Project(scenario));
            else
                missing.Add(id);
        }

        // 지문은 노드 id 를 뺀 내용으로 — 같은 배정 · 같은 시나리오면 노드가 달라도 같은 구성은 같은 지문이다.
        var content = JsonSerializer.Serialize(new { scenarios = projected, missing }, Canonical);
        var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
        return new EdgeConfig
        {
            NodeId = node.NodeId, Version = version, Scenarios = projected, MissingScenarios = missing.Count > 0 ? missing : null,
        };
    }

    internal static EdgeScenarioConfig Project(Scenario s) => new()
    {
        ScenarioId = s.ScenarioId,
        Name = s.Name,
        Type = s.Type ?? ScenarioVocabulary.Inspection,
        Trigger = s.Trigger,
        Classes = s.Classes,
        ExpectedCount = s.ExpectedCount,
        CountTolerance = s.CountTolerance,
        ExpectedByClass = s.ExpectedByClass,
        ExtraPolicy = s.ExtraPolicy,
        MinConfidence = s.MinConfidence,
        CountClasses = s.CountClasses,
    };
}
