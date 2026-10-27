using System.Security.Cryptography;
using System.Text;
using UVision.Api.Configuration;
using UVision.Api.Models;

namespace UVision.Api.Services.Vlm;

/// <summary>
/// 교사 판(判) — 같은 모델 이름이어도 프롬프트·기준 이미지·추론 설정이 달라지면 다른 교사다. 결정 장부의 VLM 단 판
/// 문자열에 새겨, 관문 0(교사 자격)이 «어느 교사가 맞혔는가»를 교사 판 단위로 잴 수 있게 한다.
/// <para>
/// 모양은 <c>{provider}:{model}|p:{8}|r:{8}</c>. <c>p</c> 는 모델이 받은 시스템 프롬프트(판정 기준 · 불량 유형 포함)와
/// 답을 바꾸는 호출 설정의 해시이고, <c>r</c> 는 기준 이미지(few-shot)의 내용 해시다. 요청마다 달라지는 기대 품번은
/// 해시에서 뺀다 — 안 그러면 오투입 확인의 교사 판이 요청마다 갈라진다. 호출 시간 한도는 답의 내용을 바꾸지 않아 뺀다.
/// 장부에 이 접미가 없는 옛 줄은 «교사 판 미상»으로 읽는다.
/// </para>
/// </summary>
public static class TeacherEdition
{
    /// <summary>기준 이미지가 없는 판(zero-shot) — <c>r</c> 에 적는 값.</summary>
    public const string NoReferences = "none";

    public static string Of(
        string providerModel, VlmOptions options, ScenarioContext scenario, int maxImageDimension,
        IReadOnlyList<ReferenceImage> references)
    {
        // 기대 품번은 요청 값이라 교사 판이 아니다 — 프롬프트 형태(오투입 문구 유무)는 남도록 비우지 않고 고정 자리표시로 바꾼다.
        var prompt = VlmPrompt.BuildSystemPrompt(scenario with
        {
            ExpectedPart = scenario.ExpectedPart is null ? null : "-",
        });
        var settings = string.Join('\n',
            prompt,
            $"max_image_dimension={maxImageDimension}",
            $"max_output_tokens={options.MaxOutputTokens}",
            $"thinking_effort={options.ThinkingEffort ?? ""}");
        return $"{providerModel}|p:{Short(Encoding.UTF8.GetBytes(settings))}|r:{ReferencesHash(references)}";
    }

    /// <summary>기준 이미지의 순서 · 라벨 · 유형 · 내용을 아우르는 해시. 순서는 모델이 본 순서 그대로다.</summary>
    private static string ReferencesHash(IReadOnlyList<ReferenceImage> references)
    {
        if (references.Count == 0)
            return NoReferences;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var r in references)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{r.Label}|{r.NgLabel}|{r.Data.Length}|"));
            hash.AppendData(r.Data.Span);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..8];
    }

    private static string Short(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..8];
}
