using System.Text.Json;
using System.Text.RegularExpressions;
using UVision.Api.Models;
using UVision.Api.Services.Scenarios;

namespace UVision.Api.Storage;

/// <summary>
/// <c>{DataPath}/{scenarioId}/scenario.json</c> 기반 파일시스템 시나리오 저장소(CRUD).
/// </summary>
public sealed partial class FileScenarioStore : IScenarioStore
{
    // JSONL 은 한 줄이 한 레코드다 — 디스크 정본의 들여쓰기 옵션을 줄 추가에는 쓰지 않는다.
    private static readonly JsonSerializerOptions LineJson = new(StoragePaths.Json) { WriteIndented = false };

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
        await RecordTuningAsync<double>(scenarioId, "memory_max_distance", null, scenario.MemoryMaxDistance, cancellationToken);
        await RecordTuningAsync<bool>(scenarioId, "allow_cloud_egress", false, scenario.AllowCloudEgress, cancellationToken); // 만들 때의 기준은 deny
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
        var counting = CountingFields.Merge(input, existing, typeChanged);
        var stream = StreamFields.Merge(input, existing, typeChanged);
        var scenario = input.ToScenario(scenarioId) with // id 불변
        {
            Type = input.Type ?? existing?.Type,
            Trigger = input.Trigger ?? (typeChanged ? null : existing?.Trigger), // 유형에 묶인 trigger(stream)는 유형이 바뀌면 옮기지 않는다
            Classes = input.Classes ?? (typeChanged ? null : existing?.Classes),
            Judges = input.Judges ?? (typeChanged ? null : existing?.Judges),
            Trust = input.Trust ?? existing?.Trust,
            // 기준 이미지의 NG 유형 설명은 생략(비어 있음) = 보존 — 이름 · 기준만 고친 수정이 기준 이미지의 설명을 지우지 않는다.
            // 한 장의 설명은 기준 이미지 쪽(SetNgLabelAsync)에서 고치고 지운다.
            NgLabels = input.NgLabels.Count > 0 ? input.NgLabels : existing?.NgLabels ?? new Dictionary<string, string>(),
            // 생략 = 보존, 빈 문자열 = 지움(전역 설정으로).
            // 외부 송출 허용: 생략 = 보존, 값 = 교체(true · false 모두). 말없이 꺼지거나 켜지지 않는다 — 바뀌면 이력에 남는다.
            AllowCloudEgress = input.AllowCloudEgress ?? existing?.AllowCloudEgress ?? false,
            PatchEncoder = input.PatchEncoder is { } encoder ? (encoder.Length == 0 ? null : encoder) : existing?.PatchEncoder,
            // 생략 = 보존, 0 = 지움. 유형이 바뀌면 옮기지 않는다(검사에서 다른 유형으로 바꿨는데 문턱이 남지 않게).
            MemoryMaxDistance = input.MemoryMaxDistance is { } distance ? (distance == 0 ? null : distance) : (typeChanged ? null : existing?.MemoryMaxDistance),
            // 계수 필드도 같다 — 유형이 바뀌면 옮기지 않는다(계수에서 검사로 바꿨는데 기대 개수가 남지 않게).
            ExpectedCount = counting.ExpectedCount,
            CountTolerance = counting.CountTolerance,
            ExpectedByClass = counting.ExpectedByClass,
            ExtraPolicy = counting.ExtraPolicy,
            MinConfidence = counting.MinConfidence,
            CountClasses = counting.CountClasses,
            // 스트림 감시 필드도 같다 — 유형이 바뀌면 옮기지 않는다.
            Sources = stream.Sources,
            Rules = stream.Rules,
            Outputs = stream.Outputs,
        };
        await StoragePaths.AtomicWriteJsonAsync(path, scenario, cancellationToken);
        await RecordTuningAsync<double>(scenarioId, "memory_max_distance", existing?.MemoryMaxDistance, scenario.MemoryMaxDistance, cancellationToken);
        await RecordTuningAsync<bool>(scenarioId, "allow_cloud_egress", existing?.AllowCloudEgress ?? false, scenario.AllowCloudEgress, cancellationToken);
        return scenario;
    }

    /// <summary>
    /// 옵션 레버가 실제로 바뀌었을 때만 한 줄 남긴다(<c>{scenario}/tuning.jsonl</c>, append-only) — 바꾼 값을 되돌릴 근거다.
    /// 레버는 <paramref name="option"/> 이름으로 구분한다(기억 단 거리 문턱 · 외부 송출 허용). 값이 같은 저장(다른 필드만
    /// 고친 수정)은 남기지 않는다. 기록이 실패해도 시나리오 저장은 이미 끝났으므로 던지지 않는다.
    /// </summary>
    private async Task RecordTuningAsync<T>(string scenarioId, string option, T? from, T? to, CancellationToken cancellationToken)
        where T : struct
    {
        if (EqualityComparer<T?>.Default.Equals(from, to))
            return;
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                option,
                from,
                to,
            }, LineJson);
            var logPath = _paths.TuningJsonl(scenarioId);
            // BOM 없이 — 줄 단위로 읽는 도구가 첫 줄에서 깨지지 않게(`Encoding.UTF8` 는 새 파일 맨 앞에 BOM 을 쓴다).
            await File.AppendAllTextAsync(logPath, line + "\n", new System.Text.UTF8Encoding(false), cancellationToken);
        }
        catch (IOException)
        {
            // degrade — 이력 한 줄을 못 남겨도 저장된 시나리오는 그대로 유효하다.
        }
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
