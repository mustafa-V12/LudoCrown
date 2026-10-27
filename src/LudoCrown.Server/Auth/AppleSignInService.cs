using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Auth;

/// <summary>
/// Sign in with Apple. The app uses the native Apple sheet (MAUI AppleSignInAuthenticator) and sends the
/// identity token here; the server verifies it against Apple's public keys and the app's bundle id.
///
/// Apple requires apps that offer Sign in with Apple to revoke the user's Apple tokens when they delete
/// their account. <see cref="RevokeAsync"/> does that using a fresh authorization code from the app and a
/// client secret signed with the team's .p8 key (configuration required, see docs/AUTH_SETUP.md).
/// </summary>
public sealed class AppleSignInService(IdTokenValidator validator, IOptions<AuthOptions> options, IHttpClientFactory http, TimeProvider time, ILogger<AppleSignInService> logger)
{
    public bool Enabled => options.Value.Apple.Enabled;
    public bool RevocationConfigured => options.Value.Apple.RevocationConfigured;

    public Task<IdTokenClaims?> VerifyAsync(string? identityToken, CancellationToken ct = default) =>
        validator.ValidateAsync(identityToken, new IdTokenRequirements(IdTokenValidator.AppleJwks, IdTokenValidator.AppleIssuers, options.Value.Apple.ClientIds), ct);

    /// <summary>Exchanges an authorization code for a refresh token and revokes it. Returns false if not configured or Apple rejected it.</summary>
    public async Task<bool> RevokeAsync(string authorizationCode, CancellationToken ct = default)
    {
        if (!RevocationConfigured) return false;
        var a = options.Value.Apple;
        var clientId = a.ClientIds[0];
        var secret = CreateClientSecret();
        var client = http.CreateClient("idp");

        using var tokenResponse = await client.PostAsync("https://appleid.apple.com/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["code"] = authorizationCode,
            ["grant_type"] = "authorization_code",
        }), ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("Apple token exchange for revocation failed: {Status}", tokenResponse.StatusCode);
            return false;
        }
        using var doc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        var refresh = doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        if (refresh is null) return false;

        using var revokeResponse = await client.PostAsync("https://appleid.apple.com/auth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["token"] = refresh,
            ["token_type_hint"] = "refresh_token",
        }), ct);
        return revokeResponse.IsSuccessStatusCode;
    }

    /// <summary>ES256 client-secret JWT as described in Apple's "Creating a client secret" documentation.</summary>
    internal string CreateClientSecret()
    {
        var a = options.Value.Apple;
        var now = time.GetUtcNow();
        var header = TokenService.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = a.KeyId }));
        var payload = TokenService.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = a.TeamId,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(5).ToUnixTimeSeconds(),
            aud = "https://appleid.apple.com",
            sub = a.ClientIds[0],
        }));
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(a.PrivateKey!);
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{payload}.{TokenService.Base64Url(signature)}";
    }
}
