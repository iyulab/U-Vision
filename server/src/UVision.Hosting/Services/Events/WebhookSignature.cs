using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UVision.Api.Services.Events;

/// <summary>
/// Standard Webhooks 서명(https://www.standardwebhooks.com) — 받는 쪽이 출처와 재전송(타임스탬프)을 검증한다.
/// <c>webhook-signature: v1,base64(HMAC-SHA256(secret, "{id}.{timestamp}.{body}"))</c>.
/// </summary>
public static class WebhookSignature
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    /// <summary><c>whsec_</c> 접두면 뒤를 base64 로, 아니면 UTF-8 바이트로 읽는다.</summary>
    public static byte[] SecretBytes(string secret) =>
        secret.StartsWith("whsec_", StringComparison.Ordinal)
            ? Convert.FromBase64String(secret["whsec_".Length..])
            : Encoding.UTF8.GetBytes(secret);

    public static string Sign(byte[] secret, string id, long timestamp, string body)
    {
        var content = $"{id}.{timestamp.ToString(CultureInfo.InvariantCulture)}.{body}";
        var mac = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(content));
        return "v1," + Convert.ToBase64String(mac);
    }
}
