using System.Text.Json;
using System.Text.RegularExpressions;
using UVision.Api.Models;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{DataPath}/{scenarioId}/scenario.json</c> 기반 파일시스템 시나리오 저장소(CRUD).
/// </summary>
public sealed partial class FileScenarioStore : IScenarioStore
{
    private readonly StoragePaths _paths;

    public FileScenarioStore(StoragePaths paths) => _paths = paths;

    public async Task<Scenario?> GetAsync(
        string scenarioId, CancellationToken cancellationToken = default)
    {
        var path = _paths.ScenarioJson(scenarioId); // 형식 위반이면 ArgumentException(→400)
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Scenario>(
            stream, StoragePaths.Json, cancellationToken);
    }

    public async Task<IReadOnlyList<Scenario>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<Scenario>();
        if (!Directory.Exists(_paths.Root))
            return results;

        foreach (var dir in Directory.EnumerateDirectories(_paths.Root))
        {
            var jsonPath = Path.Combine(dir, "scenario.json");
            if (!File.Exists(jsonPath))
                continue;

            await using var stream = File.OpenRead(jsonPath);
            var scenario = await JsonSerializer.DeserializeAsync<Scenario>(
                stream, StoragePaths.Json, cancellationToken);
            if (scenario is not null)
                results.Add(scenario);
        }

        return results;
    }

    public async Task<Scenario> CreateAsync(
        ScenarioInput input, CancellationToken cancellationToken = default)
    {
        var scenarioId = ResolveUniqueSlug(input.Name);
        var scenario = input.ToScenario(scenarioId);
        await StoragePaths.AtomicWriteJsonAsync(
            _paths.ScenarioJson(scenarioId), scenario, cancellationToken);
        return scenario;
    }

    public async Task<Scenario?> UpdateAsync(
        string scenarioId, ScenarioInput input, CancellationToken cancellationToken = default)
    {
        var path = _paths.ScenarioJson(scenarioId); // 형식 위반이면 ArgumentException(→400)
        if (!File.Exists(path))
            return null;

        var scenario = input.ToScenario(scenarioId); // id 불변
        await StoragePaths.AtomicWriteJsonAsync(path, scenario, cancellationToken);
        return scenario;
    }

    public Task<bool> DeleteAsync(string scenarioId, CancellationToken cancellationToken = default)
    {
        var dir = _paths.ScenarioDir(scenarioId); // 형식 위반이면 ArgumentException(→400)
        if (!Directory.Exists(dir))
            return Task.FromResult(false);

        // 시나리오 + 하위 전체(기준이미지·검사 결과) 제거.
        Directory.Delete(dir, recursive: true);
        return Task.FromResult(true);
    }

    public async Task SetNgLabelAsync(
        string scenarioId, string refId, string? label, CancellationToken cancellationToken = default)
    {
        var scenario = await GetAsync(scenarioId, cancellationToken);
        if (scenario is null)
            return; // 시나리오 없음 — noop

        var labels = new Dictionary<string, string>(scenario.NgLabels);
        if (string.IsNullOrEmpty(label))
            labels.Remove(refId);
        else
            labels[refId] = label;

        await StoragePaths.AtomicWriteJsonAsync(
            _paths.ScenarioJson(scenarioId), scenario with { NgLabels = labels }, cancellationToken);
    }

    /// <summary>name → slug 도출 후 충돌 없는 id 를 고른다(<c>-2</c>, <c>-3</c> …).</summary>
    private string ResolveUniqueSlug(string name)
    {
        var baseSlug = Slugify(name);
        var slug = baseSlug;
        for (var n = 2; Directory.Exists(_paths.ScenarioDir(slug)); n++)
            slug = $"{baseSlug}-{n}";
        return slug;
    }

    /// <summary>
    /// name 을 안전한 slug 로. ASCII 영숫자만 보존(한글 등 비ASCII 는 제거되므로
    /// 비면 <c>scenario</c> fallback). <see cref="StoragePaths.Id"/> 화이트리스트와 정합.
    /// </summary>
    private static string Slugify(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        var slug = NonSlugChars().Replace(lower, "-").Trim('-');
        slug = MultiDash().Replace(slug, "-");
        return string.IsNullOrEmpty(slug) ? "scenario" : slug;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex MultiDash();
}
