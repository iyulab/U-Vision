using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UVision.Api.Configuration;

namespace UVision.Api.Storage;

/// <summary>
/// 저장소의 <b>유일한 경로 지식</b> + 파일시스템 메커니즘(sanitize·atomic write·JSON 정책).
/// 다른 어떤 곳도 <c>{DataPath}/...</c> 구조를 직접 조립하지 않는다.
///
/// 보안: id(scenarioId/imageId/refId)·date 는 디렉토리·파일명이 되므로 화이트리스트로 <b>거부</b>한다
/// (변형이 아니라 거부 — 두 id 가 한 경로로 충돌하는 것을 막는다). 경로 주입(<c>../</c>)은 화이트리스트
/// 밖이라 자연 차단된다.
/// </summary>
public sealed partial class StoragePaths
{
    private readonly string _root;

    public StoragePaths(StorageOptions options, string contentRoot)
    {
        // 상대경로는 ContentRoot 기준으로 절대화 — 실행 위치에 무관한 안정 경로.
        _root = Path.GetFullPath(options.DataPath, contentRoot);
        Directory.CreateDirectory(_root);
    }

    /// <summary>데이터 루트(절대경로).</summary>
    public string Root => _root;

    public string ScenarioDir(string scenarioId) =>
        Path.Combine(_root, Id(scenarioId));

    public string ScenarioJson(string scenarioId) =>
        Path.Combine(ScenarioDir(scenarioId), "scenario.json");

    public string DateDir(string scenarioId, string date) =>
        Path.Combine(ScenarioDir(scenarioId), Date(date));

    public string ImageFile(string scenarioId, string date, string imageId, string ext) =>
        Path.Combine(DateDir(scenarioId, date), Id(imageId) + ext);

    public string ResultFile(string scenarioId, string date, string imageId) =>
        Path.Combine(DateDir(scenarioId, date), Id(imageId) + ".json");

    /// <summary>사람 라벨 사이드카 — 결과 json 옆 <c>{image_id}.label.json</c>(가변, 정정/삭제).</summary>
    public string LabelJson(string scenarioId, string date, string imageId) =>
        Path.Combine(DateDir(scenarioId, date), Id(imageId) + ".label.json");

    /// <summary>기준 이미지 디렉토리 — <c>references/{ok|ng}/</c>. label 은 enum(닫힌 집합).</summary>
    public string ReferenceDir(string scenarioId, Models.ReferenceLabel label) =>
        Path.Combine(ScenarioDir(scenarioId), "references", label.ToString().ToLowerInvariant());

    public string ReferenceFile(
        string scenarioId, Models.ReferenceLabel label, string refId, string ext) =>
        Path.Combine(ReferenceDir(scenarioId, label), Id(refId) + ext);

    // --- sanitize (거부) ---------------------------------------------------

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex IdPattern();

    /// <summary>id 검증 — 위반 시 <see cref="ArgumentException"/>(endpoint 에서 400 으로 매핑).</summary>
    public static string Id(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value is "." or ".." || !IdPattern().IsMatch(value))
        {
            throw new ArgumentException($"유효하지 않은 식별자: '{value}'", nameof(value));
        }
        return value;
    }

    /// <summary>
    /// date 검증 — 실제 달력 날짜(yyyy-MM-dd)여야 한다. 형식뿐 아니라 의미상 유효성까지
    /// 검증한다(2026-13-99 거부). 위반 시 <see cref="ArgumentException"/>.
    /// </summary>
    public static string Date(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            throw new ArgumentException($"유효하지 않은 날짜: '{value}'", nameof(value));
        }
        return value;
    }

    // --- 파일시스템 메커니즘 ------------------------------------------------

    /// <summary>저장소 JSON 정책 — wire 와 동일 snake_case, 사람이 읽을 수 있게 들여쓰기.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        // 한글 criteria/findings 가 \uXXXX 로 이스케이프되지 않도록(파일 가독성).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// atomic 쓰기: 같은 디렉토리에 temp 작성 후 rename. 동시 읽기가 반쪽 파일을 보지 않는다.
    /// Windows 에서 기존 파일 위로의 <c>File.Move</c> 는 throw 하므로 overwrite 모드를 쓴다.
    /// </summary>
    public static async Task AtomicWriteAsync(
        string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // temp 는 반드시 같은 볼륨(같은 디렉토리)에 — cross-volume rename 은 비원자적.
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
    }

    public static Task AtomicWriteJsonAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return AtomicWriteAsync(path, bytes, ct);
    }
}
