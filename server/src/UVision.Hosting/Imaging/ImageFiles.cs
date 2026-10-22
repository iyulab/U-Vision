namespace UVision.Api.Imaging;

/// <summary>
/// 이미지 바이트의 형식 판별과 저장 확장자 — 업로드 경로(HTTP)와 판정 파이프라인이 함께 쓴다.
/// </summary>
public static class ImageFiles
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>content-type → 파일 확장자(저장 stem 과 페어). PNG 외에는 <c>.jpg</c>.</summary>
    public static string ExtensionFor(string contentType) =>
        contentType == "image/png" ? ".png" : ".jpg";

    /// <summary>
    /// 바이트가 PNG 인지 시그니처로 판별한다. 클라이언트가 붙인 content-type 이 아니라 실제 바이트를
    /// 본다 — 모델에 보낼 형식 표기는 바이트와 일치해야 하기 때문이다.
    /// </summary>
    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(PngSignature);
}
