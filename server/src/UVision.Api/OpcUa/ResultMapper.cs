using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UVision.Api.Models;

namespace UVision.Api.OpcUa;

/// <summary>
/// OPC UA 로 내보내는 판정 한 건 — 결과 관리의 <c>ResultReadyEventType</c> 필수 필드와 우리 쪽 결과 내용을 펴 놓은 모양.
/// 사건 · 조회 메서드 · 최근 결과 창이 같은 값을 쓴다.
/// </summary>
/// <param name="ResultId">판정 id — 웹훅 · MQTT 봉투의 <c>id</c> 와 같아 전송이 달라도 같은 사건을 같은 키로 거른다.</param>
/// <param name="JobId">정지 촬영은 요청 하나가 작업 하나라 판정 id 와 같다.</param>
/// <param name="InternalRecipeId">시나리오 id(결과 관리의 «레시피»).</param>
/// <param name="InternalConfigurationId">시나리오 지문 — <see cref="ScenarioFacts"/>. 시나리오를 읽을 수 없으면 빈 문자열.</param>
/// <param name="CreationTime">판정 시각(UTC).</param>
/// <param name="Verdict">wire 와 같은 <c>"OK"</c>/<c>"NG"</c> 문자열.</param>
/// <param name="Class">확정한 클래스 id.</param>
/// <param name="SettledByTier">판정을 낸 단.</param>
/// <param name="SettledByKind">판정자 종류.</param>
/// <param name="RequiresReview">사람 검토가 필요한 건 — 결과 v2 에 없으면 <c>false</c>.</param>
/// <param name="Count">계수 판정에서 센 개수 — 계수가 아니면 <c>null</c>.</param>
/// <param name="Json">결과 v2 전체 — CloudEvents 봉투 <c>data</c> 와 byte 같다(다시 직렬화하지 않고 원문을 그대로 떼어 온다).</param>
public sealed record OpcUaResult(
    string ResultId,
    string JobId,
    string InternalRecipeId,
    string InternalConfigurationId,
    bool IsPartial,
    int ResultState,
    DateTime CreationTime,
    string Verdict,
    string? Class,
    string? SettledByTier,
    string? SettledByKind,
    bool RequiresReview,
    int? Count,
    string Json)
{
    /// <summary>결과 상태 «완료» — 처리 상태이지 판정이 아니다.</summary>
    public const int StateCompleted = 1;
}

/// <summary>
/// 시나리오에서 와서 매핑에 필요한 사실 둘 — 지문(<c>InternalConfigurationId</c>)과 유형(OK/NG 보기를 정하는 데 쓴다).
/// 보내는 시점에 계산하므로, 판정과 보내기 사이에 시나리오가 고쳐졌다면 지문은 고쳐진 뒤의 값이다.
/// </summary>
public sealed record ScenarioFacts(string ConfigurationId, string? Type)
{
    public static readonly ScenarioFacts Unknown = new("", null);

    /// <summary>지문의 길이(16진 문자) — 표준이 정한 길이가 아니라 이 엔진의 선택이다.</summary>
    public const int FingerprintLength = 12;

    /// <summary>
    /// <c>scenario.json</c> 원문에서 사실을 뽑는다. 지문 = 정규화한 JSON(객체 키를 서수 순으로 정렬 · 공백 없음 · UTF-8)의
    /// sha256 앞 12자(소문자 16진) — 저장 형식을 건드리지 않고 시나리오가 바뀌면 값이 바뀌게 한다. 읽을 수 없는 JSON 이면 <see cref="Unknown"/>.
    /// </summary>
    public static ScenarioFacts FromScenarioJson(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return Unknown;
        }
        if (node is null)
            return Unknown;
        var canonical = Canonicalize(node)!.ToJsonString();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var type = node is JsonObject obj && obj.TryGetPropertyValue("type", out var t) && t is JsonValue v
            && v.TryGetValue<string>(out var s) ? s : null;
        return new ScenarioFacts(hash[..FingerprintLength], type);
    }

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Canonicalize).ToArray()),
        _ => node?.DeepClone(),
    };
}

/// <summary>
/// 판정 사건 본문(CloudEvents 봉투 JSON) → <see cref="OpcUaResult"/>. 순수 함수라 서버 없이 시험된다.
/// 봉투의 <c>data</c> 는 결과 v2(<see cref="InspectResultV2"/>)다. 판정 불가(<c>detection_unavailable</c>)는 사건이 나가지 않으므로 받지 않는다.
/// </summary>
public static class ResultMapper
{
    /// <summary>봉투의 <c>type</c> 이 엣지 노드 사건인가 — 못 읽는 본문은 false(그러면 <see cref="TryMap"/> 이 오류로 다룬다).</summary>
    public static bool IsEdgeEvent(string envelopeBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(envelopeBody);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == UVision.Api.Services.Events.EdgeEventEnvelope.EventType;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 매핑한다. 못 읽는 본문이면 사유(오류 문구)를 돌려준다 — 던지지 않는다(전송이 오류 문구로 바꿔 outbox 재시도 · 포기에 맡긴다).
    /// </summary>
    public static bool TryMap(string envelopeBody, ScenarioFacts facts, out OpcUaResult? result, out string? error)
    {
        result = null;
        try
        {
            using var doc = JsonDocument.Parse(envelopeBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                error = "malformed event: no data object";
                return false;
            }
            var id = String(data, "decision_id") ?? String(root, "id");
            var scenarioId = String(data, "scenario_id");
            var timestamp = String(data, "timestamp") ?? String(root, "time");
            var cls = String(data, "class");
            if (id is null || scenarioId is null || timestamp is null || cls is null)
            {
                error = "malformed event: decision_id, scenario_id, timestamp and class are required";
                return false;
            }
            if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                error = "malformed event: unreadable timestamp";
                return false;
            }
            string? tier = null, kind = null;
            if (data.TryGetProperty("settled_by", out var settled) && settled.ValueKind == JsonValueKind.Object)
            {
                tier = String(settled, "tier");
                kind = String(settled, "kind");
            }
            int? count = null;
            if (data.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Object
                && c.TryGetProperty("total", out var total) && total.TryGetInt32(out var n))
                count = n;
            var review = data.TryGetProperty("requires_review", out var r) && r.ValueKind == JsonValueKind.True;
            result = new OpcUaResult(
                ResultId: id,
                JobId: id,
                InternalRecipeId: scenarioId,
                InternalConfigurationId: facts.ConfigurationId,
                IsPartial: false,
                ResultState: OpcUaResult.StateCompleted,
                CreationTime: at.UtcDateTime,
                Verdict: VerdictOf(cls, facts.Type),
                Class: cls,
                SettledByTier: tier,
                SettledByKind: kind,
                RequiresReview: review,
                Count: count,
                // 다시 직렬화하지 않는다 — 봉투 data 의 원문 바이트 그대로.
                Json: data.GetRawText());
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = "malformed event: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 클래스의 OK/NG 보기 — 판정 파이프라인의 v1 보기와 같은 규칙이다(좋은 쪽만 OK, 불량 유형은 모두 NG).
    /// 결과 v2 에는 verdict 필드가 없어 시나리오 유형(오투입 확인은 <c>MATCH</c> 가 좋은 쪽)을 같이 받는다.
    /// </summary>
    public static string VerdictOf(string classId, string? scenarioType)
    {
        var good = scenarioType == ScenarioVocabulary.Verification ? ScenarioVocabulary.Match : ScenarioVocabulary.Ok;
        return string.Equals(classId, good, StringComparison.OrdinalIgnoreCase) ? ScenarioVocabulary.Ok : ScenarioVocabulary.Ng;
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
