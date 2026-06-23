using UVision.Api.Models;

namespace UVision.Api.Services.Oracle;

/// <summary>오라클 비활성(none) — 스윕이 IsEnabled 로 차단하므로 SecondOpinionAsync 는 호출되지 않는다.</summary>
public sealed class DisabledOracleProvider : IOracleProvider
{
    public string Name => "none";
    public bool IsEnabled => false;
    public bool IsCloud => false;
    public Task<InspectionResult> SecondOpinionAsync(
        ReadOnlyMemory<byte> image, ScenarioContext scenario, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("오라클 비활성 — SecondOpinionAsync 호출 금지");
}
