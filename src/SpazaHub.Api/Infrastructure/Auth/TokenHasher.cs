using System.Security.Cryptography;

namespace SpazaHub.Api.Auth;

/// <summary>
/// Opaque refresh token generation and lookup hashing. Tokens are 64 random bytes;
/// only the SHA-256 hash is persisted.
/// </summary>
public static class TokenHasher
{
    public static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public static string HashToken(string token)
        => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
