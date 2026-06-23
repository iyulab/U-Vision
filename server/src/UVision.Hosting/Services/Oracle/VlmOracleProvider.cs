using UVision.Api.Models;
using UVision.Api.Services.Vlm;

namespace UVision.Api.Services.Oracle;

/// <summary>IVlmProvider("더 센 VLM")에 위임하는 오라클 어댑터 — IronHiveVlmProvider 재사용.</summary>
public sealed class VlmOracleProvider : IOracleProvider
{
    private readonly IVlmProvider _inner;
    public VlmOracleProvider(IVlmProvider inner, bool isCloud) { _inner = inner; IsCloud = isCloud; }

    public string Name => $"oracle:{_inner.Name}";
    public bool IsEnabled => true;
    public bool IsCloud { get; }

    public Task<InspectionResult> SecondOpinionAsync(
        ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(image, scenario, cancellationToken);
}
