using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// 검사 한 건의 이상 지도 <c>{date}/anomaly-maps/{image_id}.json</c> — 결과 json 옆이 아니라 하위 폴더에 둔다(결과 목록이
    /// 날짜 폴더의 <c>*.json</c> 을 결과로 읽는다). 이미지에서 다시 만들 수 있는 파생물이 아니라 판정 당시 기억의 값이다.
    /// </summary>
    public string AnomalyMapFile(string scenarioId, string date, string imageId) =>
        Path.Combine(DateDir(scenarioId, date), "anomaly-maps", Id(imageId) + ".json");

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

    // --- 데이터셋 export (신뢰성 플라이휠 ②) -------------------------------
    // 사람 라벨된 이미지를 MLoop 이미지분류 입력 레이아웃으로 스냅샷한다.
    // <c>datasets</c> 는 날짜로 파싱되지 않으므로 ListDatesAsync 가 자연 제외한다(references 와 동일).

    private const string DatasetsSegment = "datasets";

    /// <summary>시나리오의 모든 export 를 담는 디렉토리 <c>{scenario}/datasets</c>.</summary>
    public string DatasetsDir(string scenarioId) =>
        Path.Combine(ScenarioDir(scenarioId), DatasetsSegment);

    /// <summary>단일 export 디렉토리 <c>{scenario}/datasets/{exportId}</c>.</summary>
    public string DatasetDir(string scenarioId, string exportId) =>
        Path.Combine(DatasetsDir(scenarioId), Id(exportId));

    /// <summary>학습기가 가리킬 이미지 루트 <c>{scenario}/datasets/{exportId}/images</c>.</summary>
    public string DatasetImagesRoot(string scenarioId, string exportId) =>
        Path.Combine(DatasetDir(scenarioId, exportId), "images");

    /// <summary>클래스 디렉토리 <c>.../images/{classDir}</c>. 폴더명 = MLoop class label.</summary>
    public string DatasetClassDir(string scenarioId, string exportId, string classDir) =>
        Path.Combine(DatasetImagesRoot(scenarioId, exportId), Id(classDir));

    /// <summary>export manifest <c>{scenario}/datasets/{exportId}/manifest.json</c>.</summary>
    public string DatasetManifest(string scenarioId, string exportId) =>
        Path.Combine(DatasetDir(scenarioId, exportId), "manifest.json");

    // --- 관측성 메트릭 (신뢰성 플라이휠 B3) ---------------------------------
    // inspect 예측 신호를 날짜별 jsonl 시계열로. <c>metrics</c> 는 날짜로 파싱되지 않으므로
    // ListDatesAsync 가 자연 제외한다(references·datasets 와 동일).

    private const string MetricsSegment = "metrics";

    /// <summary>시나리오의 메트릭 디렉토리 <c>{scenario}/metrics</c>.</summary>
    public string MetricsDir(string scenarioId) =>
        Path.Combine(ScenarioDir(scenarioId), MetricsSegment);

    /// <summary>날짜 버킷 메트릭 jsonl <c>{scenario}/metrics/{yyyy-MM-dd}.jsonl</c>(append-only).</summary>
    public string MetricsJsonl(string scenarioId, string date) =>
        Path.Combine(MetricsDir(scenarioId), Date(date) + ".jsonl");

    // --- 결정 장부 (판정 라우터 provenance) --------------------------------
    // 판정 하나마다 HoneAI PredictionProvenance 한 줄(누가 답했고, 어느 단이 Shadow 였고, 실제로 쓴 답은
    // 무엇이었나). 관문 판정의 원천 자료라 append-only. <c>ledger</c> 도 날짜로 파싱되지 않아 목록에서 빠진다.

    private const string LedgerSegment = "ledger";

    /// <summary>날짜 버킷 결정 장부 jsonl <c>{scenario}/ledger/{yyyy-MM-dd}.jsonl</c>(append-only).</summary>
    public string LedgerJsonl(string scenarioId, string date) =>
        Path.Combine(ScenarioDir(scenarioId), LedgerSegment, Date(date) + ".jsonl");

    // --- 확정 사례 기억 (system 1 임베딩 단) ----------------------------------
    // 사람이 확정한 라벨 이미지의 임베딩 벡터 캐시 — 인코더 판(encoderKey)마다 따로 둔다. 판이 다른 벡터는 서로
    // 비교할 수 없기 때문이다. 이미지 · 라벨 정본에서 다시 만들 수 있는 파생물이라 지워도 된다. 클래스는 여기 적지
    // 않는다(읽을 때 현재 라벨에서 잇는다). <c>memory</c> 도 날짜로 파싱되지 않아 목록에서 빠진다.

    private const string MemorySegment = "memory";

    /// <summary>인코더 판의 벡터 캐시 jsonl <c>{scenario}/memory/{encoderKey}/vectors.jsonl</c>(append-only).</summary>
    public string MemoryVectorsJsonl(string scenarioId, string encoderKey) =>
        Path.Combine(ScenarioDir(scenarioId), MemorySegment, Id(encoderKey), "vectors.jsonl");

    /// <summary>
    /// 정상 사례 한 장의 패치 벡터 <c>{scenario}/memory/{encoderKey}/patches/{imageId}.f32</c> — little-endian float32,
    /// 패치 수 × 차원이 이어 붙은 하나의 배열. 벡터 캐시와 같은 판 디렉터리에 있어 판이 바뀌면 함께 버려진다.
    /// </summary>
    public string MemoryPatchFile(string scenarioId, string encoderKey, string imageId) =>
        Path.Combine(ScenarioDir(scenarioId), MemorySegment, Id(encoderKey), "patches", Id(imageId) + ".f32");

    // --- 판정 사건 outbox (P4 이벤트 내보내기) -------------------------------
    // 받는 곳마다 보낼 사건 파일 하나씩. 보내면 지우고, 끝내 못 보내면 dead/ 로 옮긴다. <c>events</c> 도 날짜로
    // 파싱되지 않아 목록에서 빠진다.

    private const string EventsSegment = "events";

    /// <summary>받는 곳의 outbox <c>{scenario}/events/{endpoint}</c>.</summary>
    public string EventOutboxDir(string scenarioId, string endpoint) =>
        Path.Combine(ScenarioDir(scenarioId), EventsSegment, Id(endpoint));

    /// <summary>보내기를 포기한 사건 <c>{scenario}/events/{endpoint}/dead</c>.</summary>
    public string EventDeadDir(string scenarioId, string endpoint) =>
        Path.Combine(EventOutboxDir(scenarioId, endpoint), "dead");

    // --- 재전송 응답 (멱등 키) ------------------------------------------------
    // 키를 단 검사 요청의 성공 응답 본문 — 같은 키가 다시 오면 판정을 다시 돌리지 않고 이것을 돌려준다. 키 하나에
    // 작은 파일 하나(검사 레코드와 같은 규모)이고 아직 지우지 않는다. 키의 해시 앞 두 글자로 디렉터리를 나눠 한
    // 디렉터리가 끝없이 커지지 않게 한다. <c>idempotency</c> 도 날짜로 파싱되지 않아 목록에서 빠진다.

    private const string IdempotencySegment = "idempotency";

    /// <summary>시나리오의 재전송 응답 디렉터리 <c>{scenario}/idempotency</c>.</summary>
    public string ReplayDir(string scenarioId) =>
        Path.Combine(ScenarioDir(scenarioId), IdempotencySegment);

    /// <summary>키의 재전송 응답 <c>{scenario}/idempotency/{hh}/{key}.json</c>(hh = 키 SHA-256 앞 두 hex).</summary>
    public string ReplayFile(string scenarioId, string key) =>
        Path.Combine(ReplayDir(scenarioId),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Id(key))))[..2], key + ".json");

    /// <summary>모든 시나리오 디렉터리(사건 보내기가 outbox 를 찾는 범위).</summary>
    public IEnumerable<string> ScenarioIds() =>
        Directory.Exists(_root)
            ? Directory.EnumerateDirectories(_root).Select(Path.GetFileName).OfType<string>()
            : [];

    // --- 모델 버저닝 (신뢰성 플라이휠 B1) ----------------------------------
    // 모델 바이너리는 MLoop serve 소유. 여기서는 참조·provenance manifest + active 포인터만.
    // <c>models</c> 는 날짜로 파싱되지 않으므로 ListDatesAsync 가 자연 제외한다(references·datasets·metrics 와 동일).

    private const string ModelsSegment = "models";

    /// <summary>시나리오의 모델 디렉터리 <c>{scenario}/models</c>.</summary>
    public string ModelsDir(string scenarioId) =>
        Path.Combine(ScenarioDir(scenarioId), ModelsSegment);

    /// <summary>단일 모델 버전 디렉터리 <c>{scenario}/models/{version}</c>.</summary>
    public string ModelVersionDir(string scenarioId, string version) =>
        Path.Combine(ModelsDir(scenarioId), Id(version));

    /// <summary>버전 manifest <c>{scenario}/models/{version}/manifest.json</c>(불변).</summary>
    public string ModelManifest(string scenarioId, string version) =>
        Path.Combine(ModelVersionDir(scenarioId, version), "manifest.json");

    /// <summary>active 포인터 <c>{scenario}/models/active.json</c>(가변·atomic 교체).</summary>
    public string ModelPointerFile(string scenarioId) =>
        Path.Combine(ModelsDir(scenarioId), "active.json");

    // --- 권한 이양 단계 (신뢰성 플라이휠 A1) --------------------------------

    /// <summary>권한 이양 단계 상태 <c>{scenario}/authority.json</c>(가변·atomic 교체, A1).</summary>
    public string AuthorityFile(string scenarioId) =>
        Path.Combine(ScenarioDir(scenarioId), "authority.json");

    // --- 관리자 PIN 부트스트랩 (first-run) ----------------------------------
    // 파일시스템을 신뢰 루트로 삼는 최초 PIN 설정. 두 파일 모두 데이터 루트 직속(시나리오 무관 전역
    // 상태). authority.json·active.json 과 동일한 소유권 모델(atomic 교체).

    /// <summary>부트스트랩된 관리자 PIN 해시 <c>{DataPath}/admin-pin.json</c>(가변·atomic 교체).</summary>
    public string AdminPinFile() => Path.Combine(_root, "admin-pin.json");

    /// <summary>
    /// 일회용 최초 설정 토큰 <c>{DataPath}/admin-setup-token.txt</c> — 서버 머신 파일시스템 접근을
    /// 관리 권한 부트스트랩의 신뢰 루트로 삼는다(네트워크상 선점 탈취 방지). 설정 완료 시 삭제.
    /// </summary>
    public string AdminSetupTokenFile() => Path.Combine(_root, "admin-setup-token.txt");

    // --- sanitize (거부) ---------------------------------------------------

    // \z — $ 는 끝 줄바꿈 앞에서도 맞아 "id\n" 을 통과시킨다(디렉토리 이름에 줄바꿈).
    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z")]
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

    /// <summary>
    /// ISO-8601 timestamp 에서 날짜 버킷(yyyy-MM-dd, UTC)을 도출한다 — 캡처·결과·메트릭이 같은 날짜
    /// 디렉토리/파일에 모이도록 보장하는 유일 규약. 경로 버킷팅은 경로 지식이므로 여기(StoragePaths)가 소유.
    /// </summary>
    public static string DateBucketOf(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
