using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvocorCommander.Api;

/// <summary>
/// Lightweight JWT (HS256) service — no NuGet dependency.
/// Generates and validates tokens using HMAC-SHA256.
/// </summary>
public sealed class JwtService
{
    private readonly byte[] _secretKey;
    private readonly TimeSpan _expiry = TimeSpan.FromDays(30);

    public JwtService(string? secret = null)
    {
        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException(
                "JWT secret is not configured. SeedDefaults should have created 'jwt.secret' in ServerConfig.");

        _secretKey = Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>Generates a JWT token for the given username and role.</summary>
    public string GenerateToken(string username, string role)
    {
        var header = new { alg = "HS256", typ = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["sub"]  = username,
            ["role"] = role,
            ["iat"]  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"]  = DateTimeOffset.UtcNow.Add(_expiry).ToUnixTimeSeconds(),
            ["iss"]  = "AvocorCommander",
        };

        string headerB64  = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        string payloadB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));

        string signingInput = $"{headerB64}.{payloadB64}";
        string signature = Sign(signingInput);

        return $"{signingInput}.{signature}";
    }

    /// <summary>
    /// Validates a JWT token. Returns the claims (sub, role) if valid, null otherwise.
    /// </summary>
    public JwtClaims? ValidateToken(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        // Verify signature
        string signingInput = $"{parts[0]}.{parts[1]}";
        string expectedSig = Sign(signingInput);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedSig),
                Encoding.UTF8.GetBytes(parts[2])))
            return null;

        // Decode payload
        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64UrlDecode(parts[1]);
        }
        catch
        {
            return null;
        }

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(payloadBytes);
        }
        catch
        {
            return null;
        }

        // Check expiry
        if (payload.TryGetProperty("exp", out var expEl))
        {
            long exp = expEl.GetInt64();
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp)
                return null;
        }
        else
        {
            return null; // no expiry claim = invalid
        }

        string? sub  = payload.TryGetProperty("sub",  out var s) ? s.GetString() : null;
        string? role = payload.TryGetProperty("role", out var r) ? r.GetString() : null;

        if (string.IsNullOrEmpty(sub)) return null;

        return new JwtClaims { Username = sub, Role = role ?? "Admin" };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string Sign(string input)
    {
        using var hmac = new HMACSHA256(_secretKey);
        byte[] sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Base64UrlEncode(sigBytes);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

    private static byte[] Base64UrlDecode(string input)
    {
        string s = input.Replace("-", "+").Replace("_", "/");
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "=";  break;
        }
        return Convert.FromBase64String(s);
    }
}

public sealed class JwtClaims
{
    public string Username { get; set; } = "";
    public string Role     { get; set; } = "Admin";
}
