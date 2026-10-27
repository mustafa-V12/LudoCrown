using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LudoCrown.Server.Auth;

/// <summary>Provides public signing keys (JWKS) for an identity provider.</summary>
public interface IJwksSource
{
    /// <summary>Returns the RSA public key for a key id, or null. <paramref name="forceRefresh"/> bypasses the cache.</summary>
    Task<RSAParameters?> GetKeyAsync(string jwksUrl, string kid, bool forceRefresh, CancellationToken ct);
}

/// <summary>Downloads and caches JWKS documents (Apple, Google). Refetches at most once a minute on unknown key ids.</summary>
public sealed class HttpJwksSource(IHttpClientFactory httpFactory, TimeProvider time) : IJwksSource
{
    private sealed record Entry(Dictionary<string, RSAParameters> Keys, DateTimeOffset FetchedAt);
    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    public async Task<RSAParameters?> GetKeyAsync(string jwksUrl, string kid, bool forceRefresh, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        _cache.TryGetValue(jwksUrl, out var entry);
        var stale = entry is null || now - entry.FetchedAt > TimeSpan.FromHours(6);
        var mayRefetch = entry is null || now - entry.FetchedAt > TimeSpan.FromMinutes(1);
        if (stale || (forceRefresh && mayRefetch))
        {
            var json = await httpFactory.CreateClient("idp").GetStringAsync(jwksUrl, ct);
            entry = new Entry(ParseJwks(json), now);
            _cache[jwksUrl] = entry;
        }
        return entry!.Keys.TryGetValue(kid, out var key) ? key : null;
    }

    public static Dictionary<string, RSAParameters> ParseJwks(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, RSAParameters>();
        foreach (var k in doc.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (k.GetProperty("kty").GetString() != "RSA") continue;
            var kid = k.GetProperty("kid").GetString();
            if (kid is null) continue;
            result[kid] = new RSAParameters
            {
                Modulus = TokenService.FromBase64Url(k.GetProperty("n").GetString()!),
                Exponent = TokenService.FromBase64Url(k.GetProperty("e").GetString()!),
            };
        }
        return result;
    }
}

public sealed record IdTokenRequirements(string JwksUrl, IReadOnlyCollection<string> Issuers, IReadOnlyCollection<string> Audiences);

/// <summary>Validated claims from a third-party ID token.</summary>
public sealed record IdTokenClaims(string Subject, string? Email, bool EmailVerified, string? Nonce, string? Name);

/// <summary>
/// Validates RS256-signed OpenID Connect ID tokens (Sign in with Apple, Google):
/// signature against the provider's JWKS, issuer, audience, expiry and issued-at.
/// </summary>
public sealed class IdTokenValidator(IJwksSource jwks, TimeProvider time)
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    public async Task<IdTokenClaims?> ValidateAsync(string? token, IdTokenRequirements req, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 8192) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var header = JsonDocument.Parse(TokenService.FromBase64Url(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256") return null;
            var kid = header.RootElement.GetProperty("kid").GetString();
            if (string.IsNullOrEmpty(kid)) return null;

            var key = await jwks.GetKeyAsync(req.JwksUrl, kid, false, ct) ?? await jwks.GetKeyAsync(req.JwksUrl, kid, true, ct);
            if (key is null) return null;

            using var rsa = RSA.Create();
            rsa.ImportParameters(key.Value);
            var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            if (!rsa.VerifyData(signed, TokenService.FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return null;

            using var payload = JsonDocument.Parse(TokenService.FromBase64Url(parts[1]));
            var root = payload.RootElement;
            var now = time.GetUtcNow();

            if (!req.Issuers.Contains(root.GetProperty("iss").GetString())) return null;
            if (!AudienceMatches(root.GetProperty("aud"), req.Audiences)) return null;
            if (DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64()) < now - Skew) return null;
            if (root.TryGetProperty("iat", out var iat) && DateTimeOffset.FromUnixTimeSeconds(iat.GetInt64()) > now + Skew) return null;

            var sub = root.GetProperty("sub").GetString();
            if (string.IsNullOrEmpty(sub)) return null;
            var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            var verified = root.TryGetProperty("email_verified", out var ev) &&
                           (ev.ValueKind == JsonValueKind.True || (ev.ValueKind == JsonValueKind.String && ev.GetString() == "true"));
            var nonce = root.TryGetProperty("nonce", out var n) ? n.GetString() : null;
            var name = root.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            return new IdTokenClaims(sub, email, verified, nonce, name);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException)
        {
            return null;
        }
    }

    private static bool AudienceMatches(JsonElement aud, IReadOnlyCollection<string> allowed) => aud.ValueKind switch
    {
        JsonValueKind.String => allowed.Contains(aud.GetString()),
        JsonValueKind.Array => aud.EnumerateArray().Any(a => allowed.Contains(a.GetString())),
        _ => false,
    };

    public static readonly IReadOnlyCollection<string> AppleIssuers = new[] { "https://appleid.apple.com" };
    public const string AppleJwks = "https://appleid.apple.com/auth/keys";
    public static readonly IReadOnlyCollection<string> GoogleIssuers = new[] { "https://accounts.google.com", "accounts.google.com" };
    public const string GoogleJwks = "https://www.googleapis.com/oauth2/v3/certs";
}
