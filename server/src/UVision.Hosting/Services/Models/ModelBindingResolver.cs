using Microsoft.Extensions.Logging;
using UVision.Api.Models;
using UVision.Api.Storage;

namespace UVision.Api.Services.Models;

/// <summary>
/// inspect 핫패스가 소비하는 active 모델 바인딩 해석(신뢰성 플라이휠 B1). active 포인터 → manifest →
/// <see cref="ModelBinding"/>. 미등록·dangling·읽기 실패는 <b>전부 null 폴백</b>(degrade-safe) —
/// 호출 측(MloopClassifier)이 전역 MlOptions.Model 로 폴백하여 현재 동작을 보존한다.
/// </summary>
public sealed class ModelBindingResolver
{
    private readonly IModelRegistry _registry;
    private readonly ILogger<ModelBindingResolver> _logger;

    public ModelBindingResolver(IModelRegistry registry, ILogger<ModelBindingResolver> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<ModelBinding?> ResolveAsync(string scenarioId, CancellationToken ct = default)
    {
        try
        {
            var pointer = await _registry.ReadPointerAsync(scenarioId, ct);
            if (pointer is null) return null;

            var manifest = await _registry.ReadManifestAsync(scenarioId, pointer.ActiveVersion, ct);
            if (manifest is null)
            {
                _logger.LogWarning(
                    "active 포인터가 가리키는 모델 버전 없음(dangling) — 전역 폴백 (scenario={ScenarioId}, version={Version})",
                    scenarioId, pointer.ActiveVersion);
                return null;
            }
            return new ModelBinding { Version = manifest.Version, ModelName = manifest.ModelName };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "모델 바인딩 해석 실패 — 전역 폴백 (scenario={ScenarioId})", scenarioId);
            return null;
        }
    }
}
