using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LudoCrown.Server.Data;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Auth;

/// <summary>Identity returned by a provider after the code exchange.</summary>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name);

/// <summary>Exchanges an OAuth authorization code for a verified identity. One per provider.</summary>
public interface IOAuthProvider
{
    string Name { get; }
    bool Enabled { get; }
    string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge);
    Task<ExternalIdentity?> ExchangeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct);
}

public sealed class GoogleOAuthProvider(IOptions<AuthOptions> options, IHttpClientFactory http, IdTokenValidator validator) : IOAuthProvider
{
    public string Name => AuthProviders.Google;
    public bool Enabled => options.Value.Google.Enabled;

    public string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge) =>
        "https://accounts.google.com/o/oauth2/v2/auth" + Query(new()
        {
            ["client_id"] = options.Value.Google.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        });

    public async Task<ExternalIdentity?> ExchangeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var g = options.Value.Google;
        using var response = await http.CreateClient("idp").PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = g.ClientId,
            ["client_secret"] = g.ClientSecret!,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier,
        }), ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var idToken = doc.RootElement.TryGetProperty("id_token", out var t) ? t.GetString() : null;
        var claims = await validator.ValidateAsync(idToken, new IdTokenRequirements(IdTokenValidator.GoogleJwks, IdTokenValidator.GoogleIssuers, new[] { g.ClientId }), ct);
        return claims is null ? null : new ExternalIdentity(claims.Subject, null, claims.Name);
    }

    internal static string Query(Dictionary<string, string> values) =>
        "?" + string.Join("&", values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
}

public sealed class FacebookOAuthProvider(IOptions<AuthOptions> options, IHttpClientFactory http) : IOAuthProvider
{
    public string Name => AuthProviders.Facebook;
    public bool Enabled => options.Value.Facebook.Enabled;
    private string Version => options.Value.FacebookGraphVersion;

    public string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge) =>
        $"https://www.facebook.com/{Version}/dialog/oauth" + GoogleOAuthProvider.Query(new()
        {
            ["client_id"] = options.Value.Facebook.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "public_profile",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });

    public async Task<ExternalIdentity?> ExchangeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var f = options.Value.Facebook;
        var client = http.CreateClient("idp");
        var tokenUrl = $"https://graph.facebook.com/{Version}/oauth/access_token" + GoogleOAuthProvider.Query(new()
        {
            ["client_id"] = f.ClientId,
            ["client_secret"] = f.ClientSecret!,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
        });
        using var tokenResponse = await client.GetAsync(tokenUrl, ct);
        if (!tokenResponse.IsSuccessStatusCode) return null;
        using var tokenDoc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;

        // appsecret_proof proves the call comes from our server, not a stolen token elsewhere.
        var proof = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(f.ClientSecret!), Encoding.UTF8.GetBytes(accessToken)));
        using var me = await client.GetAsync($"https://graph.facebook.com/{Version}/me" + GoogleOAuthProvider.Query(new()
        {
            ["fields"] = "id,name",
            ["access_token"] = accessToken,
            ["appsecret_proof"] = proof,
        }), ct);
        if (!me.IsSuccessStatusCode) return null;
        using var meDoc = JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct));
        var id = meDoc.RootElement.GetProperty("id").GetString();
        return string.IsNullOrEmpty(id) ? null : new ExternalIdentity(id, null, meDoc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null);
    }
}

/// <summary>
/// Server-side OAuth 2.0 authorization-code flow with PKCE for Google and Facebook.
/// The app opens /auth/oauth/{provider}/start in the system browser (ASWebAuthenticationSession via MAUI
/// WebAuthenticator). After the provider redirects back here, the server verifies the identity and sends the
/// app a single-use, 2-minute login code via {scheme}://auth?code=..., which the app swaps for tokens.
/// Tokens never appear in URLs, and the client secret never leaves the server.
/// </summary>
public sealed class OAuthService(
    IEnumerable<IOAuthProvider> providers,
    IAccountStore store,
    AccountService accounts,
    TokenService tokens,
    IOptions<AuthOptions> options,
    TimeProvider time,
    ILogger<OAuthService> logger)
{
    private sealed record StatePayload(string Provider, string Verifier, Guid? LinkUserId);

    public IOAuthProvider? Find(string provider) => providers.FirstOrDefault(p => p.Name == provider && p.Enabled);

    public string RedirectUri(string provider) => $"{options.Value.PublicBaseUrl.TrimEnd('/')}/auth/oauth/{provider}/callback";

    /// <summary>Returns the provider URL to redirect to, or null if the provider/link ticket is invalid.</summary>
    public async Task<string?> StartAsync(string provider, string? linkTicket, CancellationToken ct = default)
    {
        var p = Find(provider);
        if (p is null) return null;
        var now = time.GetUtcNow();

        Guid? linkUser = null;
        if (!string.IsNullOrEmpty(linkTicket))
        {
            var ticket = await store.ConsumeAuthCodeAsync(AuthCodeKinds.LinkTicket, TokenService.Hash(linkTicket), now, ct);
            if (ticket?.UserId is null) return null;
            linkUser = ticket.UserId;
        }

        var state = TokenService.Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = TokenService.Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = TokenService.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var payload = JsonSerializer.Serialize(new StatePayload(provider, verifier, linkUser));
        await store.CreateAuthCodeAsync(new AuthCodeRecord(Guid.NewGuid(), AuthCodeKinds.OAuthState, TokenService.Hash(state), linkUser, payload, now.AddMinutes(10), 0), ct);
        return p.BuildAuthorizeUrl(RedirectUri(provider), state, challenge);
    }

    /// <summary>Handles the provider callback and returns the app URL to redirect to (with a login code or an error).</summary>
    public async Task<string> CallbackAsync(string provider, string? code, string? state, string? error, CancellationToken ct = default)
    {
        var app = options.Value.AppCallbackUrl;
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return $"{app}?error=cancelled";

        var now = time.GetUtcNow();
        var record = await store.ConsumeAuthCodeAsync(AuthCodeKinds.OAuthState, TokenService.Hash(state), now, ct);
        if (record?.Payload is null) return $"{app}?error=expired";
        var payload = JsonSerializer.Deserialize<StatePayload>(record.Payload)!;
        var p = Find(provider);
        if (p is null || payload.Provider != provider) return $"{app}?error=provider";

        ExternalIdentity? identity;
        try
        {
            identity = await p.ExchangeAsync(code, RedirectUri(provider), payload.Verifier, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            logger.LogWarning(ex, "OAuth exchange failed for {Provider}", provider);
            identity = null;
        }
        if (identity is null) return $"{app}?error=verification";

        var result = await accounts.SignInWithIdentityAsync(provider, identity.Subject, identity.Email, identity.Name, payload.LinkUserId, ct);
        if (result.Outcome == SignInOutcome.IdentityBelongsToAnotherAccount) return $"{app}?error=already_linked";
        if (result.Tokens is null) return $"{app}?error=account";

        // Hand off via a single-use code; the refresh token issued above is discarded in favour of a fresh pair at exchange.
        await tokens.RevokeAsync(result.Tokens.RefreshToken, ct);
        var loginCode = TokenService.Base64Url(RandomNumberGenerator.GetBytes(24));
        await store.CreateAuthCodeAsync(new AuthCodeRecord(Guid.NewGuid(), AuthCodeKinds.LoginCode, TokenService.Hash(loginCode), result.Tokens.UserId, null, now.AddMinutes(2), 0), ct);
        return $"{app}?code={Uri.EscapeDataString(loginCode)}";
    }

    public async Task<TokenPair?> ExchangeLoginCodeAsync(string? loginCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(loginCode) || loginCode.Length > 100) return null;
        var record = await store.ConsumeAuthCodeAsync(AuthCodeKinds.LoginCode, TokenService.Hash(loginCode), time.GetUtcNow(), ct);
        if (record?.UserId is not Guid userId) return null;
        var user = await store.GetUserAsync(userId, ct);
        return user is null ? null : await tokens.IssueAsync(userId, user.IsGuest, ct);
    }

    /// <summary>Single-use ticket letting a signed-in (guest) user link a web-flow provider to their account.</summary>
    public async Task<string> CreateLinkTicketAsync(Guid userId, CancellationToken ct = default)
    {
        var ticket = TokenService.Base64Url(RandomNumberGenerator.GetBytes(24));
        await store.CreateAuthCodeAsync(new AuthCodeRecord(Guid.NewGuid(), AuthCodeKinds.LinkTicket, TokenService.Hash(ticket), userId, null, time.GetUtcNow().AddMinutes(5), 0), ct);
        return ticket;
    }
}
