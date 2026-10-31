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

    public async Task<Scenario?> CreateAsync(
        ScenarioInput input, CancellationToken cancellationToken = default)
    {
        string scenarioId;
        if (input.ScenarioId is { } requested)
        {
            scenarioId = StoragePaths.Id(requested); // 형식 위반이면 ArgumentException(→400)
            if (Directory.Exists(_paths.ScenarioDir(scenarioId)))
                return null; // 이미 있음(→409) — 정한 id 를 조용히 다른 값으로 바꾸지 않는다
        }
        else
            scenarioId = ResolveUniqueSlug(input.Name);
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

        // v2 필드는 생략(null)하면 기존 값을 보존한다 — v2 를 모르는 관리 화면이 다른 필드를 고치다 지우지 않게.
        // 시나리오 유형이 바뀌면 유형에 묶인 classes · judges 는 옮기지 않는다(검사의 OK/NG 가 오투입 확인에 남지 않게).
        var existing = await GetAsync(scenarioId, cancellationToken);
        var typeChanged = input.Type is not null
            && (input.Type ?? ScenarioVocabulary.Inspection) != (existing?.Type ?? ScenarioVocabulary.Inspection);
        var scenario = input.ToScenario(scenarioId) with // id 불변
        {
            Type = input.Type ?? existing?.Type,
            Trigger = input.Trigger ?? existing?.Trigger,
            Classes = input.Classes ?? (typeChanged ? null : existing?.Classes),
            Judges = input.Judges ?? (typeChanged ? null : existing?.Judges),
            Trust = input.Trust ?? existing?.Trust,
            // 생략 = 보존, 빈 문자열 = 지움(전역 설정으로).
            PatchEncoder = input.PatchEncoder is { } encoder ? (encoder.Length == 0 ? null : encoder) : existing?.PatchEncoder,
            // 계수 필드도 같다 — 유형이 바뀌면 옮기지 않는다(계수에서 검사로 바꿨는데 기대 개수가 남지 않게).
            ExpectedCount = input.ExpectedCount ?? (typeChanged ? null : existing?.ExpectedCount),
            CountTolerance = input.CountTolerance ?? (typeChanged ? null : existing?.CountTolerance),
            MinConfidence = input.MinConfidence ?? (typeChanged ? null : existing?.MinConfidence),
            CountClasses = input.CountClasses ?? (typeChanged ? null : existing?.CountClasses),
        };
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
