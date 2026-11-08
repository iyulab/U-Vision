using System.Diagnostics;
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

    /// <summary>
    /// 데이터 루트 직속의 예약 이름 — 엣지 노드 신원 저장소(<c>{DataPath}/edge</c>)가 쓴다. 시나리오 id 로는 쓸 수 없다
    /// (대소문자 무시: Windows 폴더는 대소문자를 구분하지 않는다).
    /// </summary>
    public const string EdgeSegment = "edge";

    public string ScenarioDir(string scenarioId)
    {
        if (string.Equals(scenarioId, EdgeSegment, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"예약된 식별자: '{scenarioId}'", nameof(scenarioId));
        return Path.Combine(_root, Id(scenarioId));
    }

    // --- 엣지 노드 신원 (원장 D-102 E-1) -------------------------------------------------
    // 일회용 등록 토큰(해시)과 노드 레코드(비밀의 해시) — 데이터 루트 직속 <c>edge/</c>. 시나리오 디렉터리가 아니다.

    /// <summary>등록 토큰 하나 <c>{DataPath}/edge/tokens/{tokenId}.json</c>.</summary>
    public string EdgeTokenFile(string tokenId) =>
        Path.Combine(_root, EdgeSegment, "tokens", Id(tokenId) + ".json");

    /// <summary>노드 레코드 디렉터리들의 부모 <c>{DataPath}/edge/nodes</c>.</summary>
    public string EdgeNodesDir() => Path.Combine(_root, EdgeSegment, "nodes");

    /// <summary>노드 레코드 <c>{DataPath}/edge/nodes/{nodeId}/node.json</c>.</summary>
    public string EdgeNodeFile(string nodeId) => Path.Combine(EdgeNodesDir(), Id(nodeId), "node.json");

    /// <summary>노드가 보낸 사건의 append-only 장부 <c>{DataPath}/edge/nodes/{nodeId}/events/{date}.jsonl</c>(날짜 = 서버가 받은 날, UTC).</summary>
    public string EdgeEventsFile(string nodeId, string date) =>
        Path.Combine(EdgeNodesDir(), Id(nodeId), "events", Date(date) + ".jsonl");

    /// <summary>사건 하나의 «받았다» 표지 <c>{DataPath}/edge/nodes/{nodeId}/seen/{앞 두 글자}/{eventId}.json</c> — 중복 제거 키 <c>(node_id, event_id)</c>.</summary>
    public string EdgeSeenFile(string nodeId, string eventId) =>
        Path.Combine(EdgeSeenDir(nodeId), Id(eventId)[..2], Id(eventId) + ".json");

    /// <summary>중복 표지 디렉터리 <c>{DataPath}/edge/nodes/{nodeId}/seen</c>.</summary>
    public string EdgeSeenDir(string nodeId) => Path.Combine(EdgeNodesDir(), Id(nodeId), "seen");

    /// <summary>받은 <c>seq</c> 구간 집합 <c>{DataPath}/edge/nodes/{nodeId}/seq.json</c>.</summary>
    public string EdgeSeqFile(string nodeId) => Path.Combine(EdgeNodesDir(), Id(nodeId), "seq.json");

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

    /// <summary>
    /// 선별 기준 이미지 폴더 — <c>references/{class}/</c>(클래스 id 소문자). 기존 <c>ok</c> · <c>ng</c> 폴더와 같은 자리에 선다 —
    /// 클래스 id 가 <c>OK</c> 면 <see cref="ReferenceDir"/> 의 ok 폴더가 곧 그 클래스다(어댑터 없이 옛 배치가 읽힌다).
    /// id 는 화이트리스트(영숫자 · '_' · '-')로 거부한다.
    /// </summary>
    public string ReferenceClassDir(string scenarioId, string classId) =>
        Path.Combine(ScenarioDir(scenarioId), "references", Id(classId).ToLowerInvariant());

    public string ReferenceClassFile(string scenarioId, string classId, string refId, string ext) =>
        Path.Combine(ReferenceClassDir(scenarioId, classId), Id(refId) + ext);

    /// <summary>시나리오의 <c>references</c> 루트 — 클래스 폴더를 모두 훑을 때.</summary>
    public string ReferencesRoot(string scenarioId) => Path.Combine(ScenarioDir(scenarioId), "references");

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

    // --- 재판정 기록 (저장된 판정에 다시 물어본 소견) ------------------------------------
    // 판정마다 «사용자가 요청한 재판정» 한 건 = 한 줄. 결정 장부와 **섞지 않는다** — 장부를 읽는 재생 · 곡선 · 문제 큐가 같은
    // 판정을 두 번 세지 않게. append-only. <c>rejudge</c> 도 날짜로 파싱되지 않아 목록에서 빠진다.

    private const string RejudgeSegment = "rejudge";

    /// <summary>날짜 버킷 재판정 jsonl <c>{scenario}/rejudge/{yyyy-MM-dd}.jsonl</c>(append-only).</summary>
    public string RejudgeJsonl(string scenarioId, string date) =>
        Path.Combine(ScenarioDir(scenarioId), RejudgeSegment, Date(date) + ".jsonl");

    // --- 보존 삭제 기록 (설치자가 구간을 골라 지운 사실) -----------------------------
    // 지울 때마다 한 줄 append — «왜 이 날의 기록이 없는가» 에 답하는 근거라 이 파일은 지우지 않는다. 시나리오 디렉터리 직속 파일이라
    // 날짜 목록에 들지 않는다.

    /// <summary>삭제 기록 <c>{scenario}/retention.jsonl</c>(append-only, 지우지 않음).</summary>
    public string RetentionJsonl(string scenarioId) =>
        Path.Combine(ScenarioDir(scenarioId), "retention.jsonl");

    // --- 옵션 조정 기록 (시나리오 레버를 바꾼 사실) -----------------------------------
    // 레버(기억 단 거리 문턱)가 바뀔 때마다 한 줄 append — 바꾼 값을 되돌릴 근거라 지우지 않는다. 시나리오 디렉터리 직속 파일이라
    // 날짜 목록에 들지 않는다.

    /// <summary>옵션 조정 기록 <c>{scenario}/tuning.jsonl</c>(append-only, 지우지 않음).</summary>
    public string TuningJsonl(string scenarioId) =>
        Path.Combine(ScenarioDir(scenarioId), "tuning.jsonl");

    /// <summary>시나리오의 날짜 폴더 이름(<c>yyyy-MM-dd</c>)과 그 경로 — 지울 날짜를 찾는 열거. 날짜가 아닌 폴더(references 등)는 빠진다.</summary>
    public IEnumerable<(string Date, string Dir)> DateDirs(string scenarioId)
    {
        var dir = ScenarioDir(scenarioId);
        if (!Directory.Exists(dir)) yield break;
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(d);
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                yield return (name, d);
        }
    }

    // --- 문제 큐 (사람이 푸는 라벨링 문제) ---------------------------------------
    // 문제 한 건마다 파일 하나 — 출제 이유 · 점수 · 답. 라벨 사이드카와 분리된 추가 기록이라 지워도 라벨은 그대로다.
    // <c>questions</c> 도 날짜로 파싱되지 않아 목록에서 빠진다.

    private const string QuestionsSegment = "questions";

    /// <summary>시나리오의 문제 디렉토리 <c>{scenario}/questions</c>.</summary>
    public string QuestionsDir(string scenarioId) =>
        Path.Combine(ScenarioDir(scenarioId), QuestionsSegment);

    /// <summary>문제 한 건 <c>{scenario}/questions/{question_id}.json</c>.</summary>
    public string QuestionJson(string scenarioId, string questionId) =>
        Path.Combine(QuestionsDir(scenarioId), Id(questionId) + ".json");

    // 측정용 흐름의 재확인 풀(HoneAI `JsonlRecheckPoolStore`)과 정답 심기 기록(`JsonlPlantStore`) — 사람이 푼 정답 심기의 근거.
    // <c>pool</c> · <c>plants</c> 도 날짜로 파싱되지 않아 목록에서 빠진다.

    /// <summary>재확인 풀 디렉토리 <c>{scenario}/pool</c>.</summary>
    public string PoolDir(string scenarioId) => Path.Combine(ScenarioDir(scenarioId), "pool");

    /// <summary>정답 심기 기록 디렉토리 <c>{scenario}/plants</c>.</summary>
    public string PlantsDir(string scenarioId) => Path.Combine(ScenarioDir(scenarioId), "plants");

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
                .Where(name => !string.Equals(name, EdgeSegment, StringComparison.OrdinalIgnoreCase))
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
    /// <para>
    /// Windows 는 누가 그 파일을 열고 있는 동안의 덮어쓰기 이동을 <see cref="UnauthorizedAccessException"/>(또는 <see cref="IOException"/>)으로
    /// 거절한다 — 읽는 쪽이 <c>FileShare.Delete</c> 를 줘도 같다(실측: 8 초 동안 읽기와 겹친 쓰기 대부분이 거절). 읽기는 몇 밀리초면 끝나므로
    /// 짧게 다시 시도한다(<see cref="ReplaceBudget"/> 동안); 그래도 안 되면 던진다. 다른 OS 에는 이 경합이 없어 첫 시도에 성공한다.
    /// </para>
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
            var started = Stopwatch.GetTimestamp();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && !BudgetSpent(started))
                {
                    await Task.Delay(attempt < 5 ? 1 : 10, ct);
                }
            }
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
    }

    /// <summary>
    /// 덮어쓰기 이동과 읽기가 겹칠 때 다시 시도하는 <b>벽시계</b> 예산(앞 5 번은 1 ms 간격, 그 뒤는 10 ms). 개수(40 번 ≈ 0.4 초)로 묶으면 부하로
    /// 이 태스크가 굶은 만큼은 세어지지 않아 전체 스위트처럼 CPU 가 바쁠 때 예산이 소진되던 일이 있었다 — 시간으로 묶으면 굶은 만큼 같이 늘어난다.
    /// 성공 경로(첫 시도)에는 영향이 없다.
    /// </summary>
    public static readonly TimeSpan ReplaceBudget = TimeSpan.FromSeconds(2);

    private static bool BudgetSpent(long startedTimestamp) => Stopwatch.GetElapsedTime(startedTimestamp) >= ReplaceBudget;

    /// <summary>
    /// 다른 쪽이 같은 파일을 교체하는 순간(<see cref="AtomicWriteAsync"/>)에 열기가 <see cref="IOException"/> 으로 거절될 수 있다 — Windows 에서만.
    /// 교체는 몇 밀리초라 짧게 다시 시도한 뒤 스트림을 돌려준다. 파일이 없으면 <see cref="FileNotFoundException"/> 그대로.
    /// </summary>
    public static async Task<FileStream> OpenReadResilientAsync(string path, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e) when (e is IOException and not FileNotFoundException and not DirectoryNotFoundException
                                          || e is UnauthorizedAccessException)
            {
                if (BudgetSpent(started)) throw;
                await Task.Delay(attempt < 5 ? 1 : 10, ct);
            }
        }
    }

    /// <summary><see cref="OpenReadResilientAsync"/> 의 동기 쌍둥이 — 비동기로 쓸 수 없는 읽기(색인 · 상태 읽기)용. 같은 공유 모드와 같은 재시도.</summary>
    public static FileStream OpenReadResilient(string path)
    {
        var started = Stopwatch.GetTimestamp();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e) when (e is IOException and not FileNotFoundException and not DirectoryNotFoundException
                                          || e is UnauthorizedAccessException)
            {
                if (BudgetSpent(started)) throw;
                Thread.Sleep(attempt < 5 ? 1 : 10);
            }
        }
    }

    /// <summary>
    /// 파일 전체를 바이트로 — 교체 순간(<see cref="AtomicWriteAsync"/>)에도 안 던지고, 같은 파일에 덧붙이는 쓰기를 막지도 않는다.
    /// <c>File.ReadAllBytesAsync</c> 는 <c>FileShare.Read</c> 로 열어 덧붙이는 쪽의 열기를 거절시킬 수 있다(Windows).
    /// </summary>
    public static async Task<byte[]> ReadAllBytesResilientAsync(string path, CancellationToken ct = default)
    {
        await using var stream = await OpenReadResilientAsync(path, ct);
        var buffer = new byte[checked((int)stream.Length)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;   // 읽는 도중 파일이 줄었다 — 읽은 만큼만
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>파일 전체를 UTF-8 텍스트로(<see cref="ReadAllBytesResilientAsync"/> 와 같은 규칙).</summary>
    public static async Task<string> ReadAllTextResilientAsync(string path, CancellationToken ct = default) =>
        System.Text.Encoding.UTF8.GetString(await ReadAllBytesResilientAsync(path, ct));

    /// <summary>
    /// 줄 단위로 — 덧붙이기 전용 파일(메트릭 · 기억 벡터 · 재판정 로그)을 읽는다. 쓰는 쪽이 열어 둔 채여도(<c>FileShare.Read</c> 로 연 덧붙이기)
    /// 읽을 수 있고, 읽는 동안 새 줄이 붙어도 그 줄은 다음 읽기가 본다. 줄바꿈 없이 끝난 마지막 줄은 그대로 돌려준다(호출자가 파싱 실패를 건너뛴다).
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadAllLinesResilientAsync(string path, CancellationToken ct = default)
    {
        await using var stream = await OpenReadResilientAsync(path, ct);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line) lines.Add(line);
        return lines;
    }

    public static Task AtomicWriteJsonAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return AtomicWriteAsync(path, bytes, ct);
    }
}
