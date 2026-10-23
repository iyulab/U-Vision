namespace UVision.Api.Services.Vlm;

/// <summary>
/// VLM · 오라클 provider 설정 값 — 받는 값은 지어진 것뿐이고, 그 밖은 기동할 때 거부한다(판정 요청이 들어와서야
/// 알게 되지 않게).
/// </summary>
public static class ProviderNames
{
    public const string Mock = "mock";
    public const string None = "none";
    public const string OpenAI = "openai";
    public const string Google = "google";

    /// <summary>OpenAI 호환 셀프호스트 서버(GPUStack · vLLM · Ollama · 게이트웨이) — 서버 주소 하나로 붙는다.</summary>
    public const string OpenAICompatible = "openai-compatible";

    public static readonly IReadOnlyList<string> Vlm = [Mock, OpenAICompatible, OpenAI, Google];

    /// <summary>오라클은 셀프호스트만 — cloud 오라클은 이미지가 밖으로 나가는 경로라 지어지지 않았다.</summary>
    public static readonly IReadOnlyList<string> Oracle = [None, OpenAICompatible];

    internal static string Unknown(string kind, string value, IReadOnlyList<string> accepted) =>
        $"알 수 없는 {kind} provider: '{value}' — 받는 값: {string.Join(" | ", accepted)}";
}
