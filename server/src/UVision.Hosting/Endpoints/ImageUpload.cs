namespace UVision.Api.Endpoints;

/// <summary>
/// 이미지 업로드 검증 공통 — <c>/api/inspect</c> 와 기준이미지 업로드가 공유한다.
/// 형식 화이트리스트·크기 상한·빈 파일 검사를 한 곳에 둔다.
/// </summary>
public static class ImageUpload
{
    public static readonly HashSet<string> AllowedTypes = new() { "image/jpeg", "image/png" };

    /// <summary>content-type → 파일 확장자(저장 stem 과 페어).</summary>
    public static string ExtensionFor(string contentType) =>
        contentType == "image/png" ? ".png" : ".jpg";

    /// <summary>
    /// 업로드 이미지를 검증한다. 위반 시 <see cref="IResult"/>(415/413/400)를 반환하고,
    /// 통과 시 <c>null</c>. 호출부는 null 일 때만 진행한다.
    /// </summary>
    public static IResult? Validate(IFormFile image, int maxUploadSizeMb)
    {
        if (!AllowedTypes.Contains(image.ContentType))
            return Results.Problem(statusCode: 415, detail: $"지원하지 않는 이미지 형식: {image.ContentType}");

        var maxBytes = maxUploadSizeMb * 1024L * 1024L;
        if (image.Length > maxBytes)
            return Results.Problem(statusCode: 413, detail: $"이미지가 너무 큼(>{maxUploadSizeMb}MB)");
        if (image.Length == 0)
            return Results.Problem(statusCode: 400, detail: "빈 이미지");

        return null;
    }

    /// <summary>업로드 스트림을 바이트로 읽는다(빈 결과면 null).</summary>
    public static async Task<byte[]?> ReadBytesAsync(IFormFile image, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, ct);
        var data = buffer.ToArray();
        return data.Length == 0 ? null : data;
    }
}
