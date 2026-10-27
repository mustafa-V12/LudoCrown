using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LudoCrown.Server.Data;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Auth;

public sealed record TokenPair(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, Guid UserId, bool IsGuest);

/// <summary>
/// Issues and validates the server's own credentials:
/// - access tokens: short-lived HS256 JWTs (sub = user id), sent as Bearer tokens;
/// - refresh tokens: random 256-bit secrets, stored only as SHA-256 hashes and rotated on every use.
/// Reusing an already-rotated refresh token revokes every session of that user (token theft signal).
/// </summary>
public sealed class TokenService
{
    public const string GuestClaim = "guest";
    private readonly byte[] _key;
    private readonly JwtOptions _options;
    private readonly IAccountStore _store;
    private readonly TimeProvider _time;

    public TokenService(IOptions<JwtOptions> options, IAccountStore store, TimeProvider time)
    {
        _options = options.Value;
        _store = store;
        _time = time;
        if (string.IsNullOrWhiteSpace(_options.SigningKey))
            throw new InvalidOperationException("Jwt:SigningKey is not configured.");
        _key = Convert.FromBase64String(_options.SigningKey);
        if (_key.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
    }

    public string CreateAccessToken(Guid userId, bool isGuest, out DateTimeOffset expiresAt)
    {
        var now = _time.GetUtcNow();
        expiresAt = now.AddMinutes(_options.AccessTokenMinutes);
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["iss"] = _options.Issuer,
            ["aud"] = _options.Audience,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            [GuestClaim] = isGuest,
        }));
        var signature = Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }

    /// <summary>Returns a principal for a valid, unexpired access token; otherwise null.</summary>
    public ClaimsPrincipal? ValidateAccessToken(string? token, string scheme)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256") return null;

            var expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[2]))) return null;

            using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
            var root = payload.RootElement;
            if (root.GetProperty("iss").GetString() != _options.Issuer) return null;
            if (root.GetProperty("aud").GetString() != _options.Audience) return null;
            var exp = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64());
            if (exp < _time.GetUtcNow().AddSeconds(-30)) return null;
            var sub = root.GetProperty("sub").GetString();
            if (!Guid.TryParse(sub, out _)) return null;
            var guest = root.TryGetProperty(GuestClaim, out var g) && g.ValueKind == JsonValueKind.True;

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, sub!),
                new Claim(GuestClaim, guest ? "true" : "false"),
            }, scheme);
            return new ClaimsPrincipal(identity);
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<TokenPair> IssueAsync(Guid userId, bool isGuest, CancellationToken ct = default)
    {
        var access = CreateAccessToken(userId, isGuest, out var exp);
        var refresh = await CreateRefreshTokenAsync(userId, ct);
        return new TokenPair(access, exp, refresh, userId, isGuest);
    }

    /// <summary>Rotates a refresh token. Returns null if it is unknown, expired, revoked, or the account is gone.</summary>
    public async Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > 200) return null;
        var now = _time.GetUtcNow();
        var record = await _store.GetRefreshTokenAsync(Hash(refreshToken), ct);
        if (record is null) return null;
        if (record.RevokedAt is not null)
        {
            // A rotated token was presented again: assume it was stolen and end all sessions.
            if (record.ReplacedBy is not null) await _store.RevokeAllRefreshTokensAsync(record.UserId, now, ct);
            return null;
        }
        if (record.ExpiresAt <= now) return null;
        var user = await _store.GetUserAsync(record.UserId, ct);
        if (user is null) return null;

        var newId = Guid.NewGuid();
        var newToken = NewSecret();
        await _store.StoreRefreshTokenAsync(new RefreshTokenRecord(newId, user.Id, Hash(newToken), now, now.AddDays(_options.RefreshTokenDays), null, null), ct);
        await _store.RevokeRefreshTokenAsync(record.Id, newId, now, ct);
        var access = CreateAccessToken(user.Id, user.IsGuest, out var exp);
        return new TokenPair(access, exp, newToken, user.Id, user.IsGuest);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        var record = await _store.GetRefreshTokenAsync(Hash(refreshToken), ct);
        if (record is not null) await _store.RevokeRefreshTokenAsync(record.Id, null, _time.GetUtcNow(), ct);
    }

    private async Task<string> CreateRefreshTokenAsync(Guid userId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var token = NewSecret();
        await _store.StoreRefreshTokenAsync(new RefreshTokenRecord(Guid.NewGuid(), userId, Hash(token), now, now.AddDays(_options.RefreshTokenDays), null, null), ct);
        return token;
    }

    public static string NewSecret() => "rt_" + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException("Invalid base64url.") };
        return Convert.FromBase64String(padded);
    }
}
