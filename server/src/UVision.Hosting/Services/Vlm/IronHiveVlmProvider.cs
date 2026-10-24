using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IronHive.Abstractions.Messages;
using IronHive.Abstractions.Messages.Content;
using UVision.Api.Imaging;
using UVision.Api.Models;

namespace UVision.Api.Services.Vlm;

/// <summary>
/// ironhive(IMessageService) 기반 VLM 어댑터 — openai-compatible(셀프호스트) · openai · google 을 모두 처리한다.
/// ironhive 가 provider 추상화·비전 입력을 흡수하므로 provider 별 구현을 분리하지 않는다.
///
/// 출력 계약은 ironhive 구조화 출력(<c>Output = typeof(InspectionResult)</c>)으로 강제한다 —
/// JsonSchemaFactory 가 발행하는 json_schema 가 grammar 로 강제되어 모델이 스키마에 맞는 JSON 을 낸다.
/// ironhive 0.7.4 가 셀프호스트 비호환(수치 union+pattern·type 없는 enum)을 정규화하기 전까지는
/// 프롬프트로 JSON 을 강제하는 app-level 우회를 썼으나, 0.7.4 에서 GPUStack(llama.cpp) 라이브 검증 완료
/// → 구조화 출력으로 복귀(우회 제거). 응답 파싱은 <see cref="ExtractJson"/> 으로 reasoning 모델의
/// 머리말/펜스를 관용 처리한다(grammar 가 content 채널을 강제하나 방어적으로 유지).
/// <para>
/// UNVERIFIED: openai · google 실호출은 검증되지 않았다(API 키 없음) — 구조화 출력의 실제 응답 형태 · few-shot
/// 판정 효과 · 지연은 키를 확보한 뒤 벤치마크 하네스로 재야 한다. 실측된 경로는 셀프호스트(GPUStack 서버)뿐이다.
/// </para>
/// </summary>
public sealed class IronHiveVlmProvider : IVlmProvider
{
    // 모델 출력 JSON 의 속성명 대소문자/순서에 관대하게. Verdict enum 의 "OK"/"NG" 변환은
    // 모델에 부착된 JsonStringEnumConverter<Verdict> 가 처리.
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // reasoning 모델(예: qwen3 계열)은 답(JSON) 앞에 사고 토큰을 소비한다. 한도가 낮으면 사고가 예산을 다 써
    // content 가 빈 채로(또는 JSON 중간에서) 끊겨(finish_reason=length) 역직렬화가 실패한다 — 그 경우를 아래에서
    // «출력 토큰 한도»로 밝혀 던진다. 한도는 설정(VlmOptions.MaxOutputTokens)이다.
    private readonly IMessageService _messageService;
    private readonly string _model;
    private readonly int _maxOutputTokens;
    private readonly MessageThinkingEffort? _thinkingEffort;

    public IronHiveVlmProvider(
        IMessageService messageService, string providerName, string model, int maxOutputTokens = 4096,
        MessageThinkingEffort? thinkingEffort = null)
    {
        _messageService = messageService;
        Name = providerName;
        _model = model;
        _maxOutputTokens = maxOutputTokens;
        _thinkingEffort = thinkingEffort;
    }

    public string Name { get; }

    public async Task<InspectionResult> InspectAsync(
        ReadOnlyMemory<byte> image,
        ScenarioContext scenario,
        CancellationToken cancellationToken = default)
    {
        var request = new MessageRequest
        {
            Provider = Name,
            Model = _model,
            System = VlmPrompt.BuildSystemPrompt(scenario),
            Messages =
            [
                Message.User([.. BuildContent(image, scenario.References, scenario.DefectClasses)]),
            ],
            // 구조화 출력(json_schema grammar 강제) — 유형을 선언한 시나리오는 유형 id 를 enum 으로 구속한 형태.
            OutputFormat = scenario.DefectClasses is { Count: > 0 } defects
                ? OutputFormat.For(TypedSchema([.. defects.Select(c => c.Id)]))
                : OutputFormat.For<InspectionResult>(),
            MaxTokens = _maxOutputTokens,      // reasoning 모델 truncation 방지(위 주석)
            ThinkingEffort = _thinkingEffort,  // null 이면 보내지 않음 — 서버 · 모델 기본값
        };

        var response = await _messageService.GenerateMessageAsync(request, cancellationToken);

        var json = (response.Message?.Content ?? [])
            .OfType<TextMessageContent>()
            .Select(c => c.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        // 한도에서 끊긴 응답은 답이 없거나 JSON 이 잘려 있다 — 원인을 «응답 없음 · 역직렬화 실패»로 가리지 않고 밝힌다.
        var truncated = response.DoneReason == MessageDoneReason.MaxTokens;
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(truncated
                ? TruncatedMessage()
                : $"VLM provider '{Name}' 가 텍스트 응답을 반환하지 않음.");
        }

        InspectionResult? result;
        try
        {
            result = ParseAnswer(ExtractJson(json), typed: scenario.DefectClasses is { Count: > 0 });
        }
        catch (JsonException) when (truncated)
        {
            throw new InvalidOperationException(TruncatedMessage());
        }
        if (result is null) throw new InvalidOperationException($"VLM 응답 역직렬화 실패: {json}");

        return result with { Confidence = NormalizeConfidence(result.Confidence) };
    }

    private string TruncatedMessage() =>
        $"VLM provider '{Name}' 의 응답이 출력 토큰 한도({_maxOutputTokens})에서 끊김 — 추론이 답 전에 한도를 다 썼다. "
        + "UVision:Vlm:MaxOutputTokens(VLM_MAX_OUTPUT_TOKENS)를 서버 문맥 길이 안에서 늘릴 것.";

    /// <summary>
    /// 유형을 나누는 판정의 출력 스키마 — <c>defect_class</c> 를 선언한 유형 id(OK 면 null)로 구속한다. 유형이
    /// 시나리오마다 달라 C# 타입에서 만들 수 없으므로 직접 짓는다.
    /// </summary>
    internal static JsonObject TypedSchema(IReadOnlyList<string> defectClasses)
    {
        var defectEnum = new JsonArray();
        foreach (var id in defectClasses) defectEnum.Add(id);
        defectEnum.Add(null);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["verdict"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("OK", "NG") },
                ["defect_class"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["enum"] = defectEnum },
                ["findings"] = new JsonObject { ["type"] = "string" },
                ["confidence"] = new JsonObject { ["type"] = "number" },
            },
            ["required"] = new JsonArray("verdict", "defect_class", "findings", "confidence"),
            ["additionalProperties"] = false,
        };
    }

    /// <summary>모델 JSON → 판정. 유형을 나누는 판정이면 <see cref="TypedAnswer"/> 로 읽어 유형을 싣는다.</summary>
    internal static InspectionResult? ParseAnswer(string json, bool typed)
    {
        if (!typed) return JsonSerializer.Deserialize<InspectionResult>(json, DeserializeOptions);
        var answer = JsonSerializer.Deserialize<TypedAnswer>(json, DeserializeOptions);
        return answer is null
            ? null
            : new InspectionResult
            {
                Verdict = answer.Verdict,
                Findings = answer.Findings ?? "",
                Confidence = answer.Confidence,
                DefectClass = answer.DefectClass,
            };
    }

    private sealed record TypedAnswer
    {
        [JsonPropertyName("verdict")] public required Verdict Verdict { get; init; }
        [JsonPropertyName("defect_class")] public string? DefectClass { get; init; }
        [JsonPropertyName("findings")] public string? Findings { get; init; }
        [JsonPropertyName("confidence")] public required double Confidence { get; init; }
    }

    /// <summary>
    /// 모델 응답 텍스트에서 JSON 객체를 관용적으로 추출한다(첫 '{' ~ 마지막 '}'). 구조화 출력의 grammar 가
    /// content 채널을 강제하므로 보통 순수 JSON 이 오지만, reasoning 모델/백엔드가 펜스·머리말을 덧붙일
    /// 가능성에 대한 defense-in-depth 다(우회 의존이 아니라 견고한 역직렬화).
    /// </summary>
    internal static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text.Trim();
    }

    /// <summary>
    /// confidence 의 도메인 불변식(0.0~1.0)을 경계에서 강제한다. 구조화 출력 json_schema 는 타입(number)만
    /// 강제하고 값 범위는 강제하지 못해, 모델이 백분율(예: 95.0)로 방출할 수 있다(프롬프트로 1차 억제하나
    /// non-deterministic). 1 초과·100 이하는 백분율로 보고 /100, 그 외는 [0,1] 로 clamp 한다.
    /// </summary>
    internal static double NormalizeConfidence(double value)
    {
        if (value > 1.0 && value <= 100.0)
        {
            value /= 100.0;
        }
        return Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// 판정 메시지 본문을 만든다. few-shot 기준 이미지가 있으면 (라벨 텍스트 + 이미지) 쌍으로
    /// 먼저 제시하고, 마지막에 판정 대상 이미지를 둔다.
    ///
    /// 판정효과 실측은 한 번뿐이다: 공개 데이터셋 VisA pcb1(공식 분할) · 셀프호스트 reasoning 모델 하나에서 OK 3 · NG 3 장을
    /// 주자 같은 시험 80장의 불량 재현율이 0.68 → 0.97, 지연 p50 은 약 +5 초. 다른 모델 · 현장 사진은 미측정(UNVERIFIED).
    /// </summary>
    internal static List<MessageContent> BuildContent(
        ReadOnlyMemory<byte> image, IReadOnlyList<ReferenceImage> references,
        IReadOnlyList<ScenarioClass>? defectClasses = null)
    {
        var content = new List<MessageContent>();

        if (references.Count > 0)
        {
            content.Add(new TextMessageContent
            {
                Value = "먼저 기준 이미지다. 이를 참고해 판정 대상을 평가하라.",
            });
            foreach (var reference in references)
            {
                content.Add(new TextMessageContent { Value = VlmPrompt.ReferenceTag(reference, defectClasses) });
                content.Add(new ImageMessageContent
                {
                    Format = reference.IsPng ? ImageFormat.Png : ImageFormat.Jpeg,
                    Base64 = Convert.ToBase64String(reference.Data.Span),
                });
            }
        }

        content.Add(new TextMessageContent { Value = "이 제품 이미지를 판정하라." });
        content.Add(new ImageMessageContent
        {
            // 다운스케일 레버가 꺼져 있으면 업로드 원본(PNG 일 수 있음)이 그대로 온다 — 바이트로 판별.
            Format = ImageFiles.IsPng(image.Span) ? ImageFormat.Png : ImageFormat.Jpeg,
            Base64 = Convert.ToBase64String(image.Span),
        });

        return content;
    }
}
