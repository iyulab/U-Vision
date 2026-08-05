using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace UVision.Api.Auth;

/// <summary>
/// 부트스트랩된 관리자 PIN 의 저장 형식 — PBKDF2 파생키(salt+iterations 자기서술).
/// 파일(<c>admin-pin.json</c>) 열람이 곧 PIN 유출이 되지 않도록 평문 대신 해시로 보관한다.
/// 단일조직 위협모델상 과할 수 있으나 비용이 미미하고 정석.
/// </summary>
public sealed record AdminPinHash
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; init; } = AdminPinHashing.Algorithm;
    [JsonPropertyName("iterations")] public required int Iterations { get; init; }
    /// <summary>base64 인코딩된 salt.</summary>
    [JsonPropertyName("salt")] public required string Salt { get; init; }
    /// <summary>base64 인코딩된 파생키.</summary>
    [JsonPropertyName("hash")] public required string Hash { get; init; }
}

/// <summary>관리자 PIN 해시 파생·검증(PBKDF2-SHA256, constant-time 비교).</summary>
public static class AdminPinHashing
{
    public const string Algorithm = "pbkdf2-sha256";

    // OWASP 2023 권고(PBKDF2-SHA256 ≥ 210k). 단일 PIN·저빈도 검증이라 여유롭게.
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    /// <summary>새 PIN 을 무작위 salt 로 해시한다.</summary>
    public static AdminPinHash Create(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return new AdminPinHash
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(key),
        };
    }

    /// <summary>제공 PIN 이 저장 해시와 일치하는지 constant-time 비교한다.</summary>
    public static bool Verify(string providedPin, AdminPinHash stored)
    {
        byte[] expected, salt;
        try
        {
            expected = Convert.FromBase64String(stored.Hash);
            salt = Convert.FromBase64String(stored.Salt);
        }
        catch (FormatException)
        {
            return false; // 손상된 파일 — 인증 실패로 취급(예외 누출 대신).
        }

        if (expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(providedPin), salt, stored.Iterations,
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
