using System.Text.Json;
using LudoCrown.App.Api;
using LudoCrown.App.Services;

namespace LudoCrown.App.Auth;

public sealed record Session(Guid UserId, bool IsGuest, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken);

public enum AuthOutcome { Success, Cancelled, NotConfigured, AlreadyLinked, Failed, Network }

public sealed record AuthResult(AuthOutcome Outcome, string? Message = null)
{
    public bool Ok => Outcome == AuthOutcome.Success;
}

/// <summary>
/// Owns the signed-in session. Tokens are kept in the iOS Keychain (<see cref="ISecureStore"/>), never in
/// plain preferences. Access tokens are refreshed automatically; signing out revokes the refresh token.
/// </summary>
public sealed class SessionService : ITokenProvider
{
    private const string StorageKey = "ludocrown.session";
    private readonly ApiClient _api;
    private readonly ISecureStore _secure;
    private readonly IAuthBrowser _browser;
    private readonly IAppleSignIn _apple;
    private readonly AppConfig _config;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Session? _session;

    public SessionService(ApiClient api, ISecureStore secure, IAuthBrowser browser, IAppleSignIn apple, AppConfig config, TimeProvider? time = null)
    {
        _api = api;
        _secure = secure;
        _browser = browser;
        _apple = apple;
        _config = config;
        _time = time ?? TimeProvider.System;
        _api.Tokens = this;
    }

    public Session? Current => _session;
    public bool IsSignedIn => _session is not null;
    public bool IsGuest => _session?.IsGuest ?? false;
    public ProvidersResponse? Providers { get; private set; }

    public event Action<Session?>? SessionChanged;

    /// <summary>Loads a saved session and refreshes it. Returns false if the user needs to sign in.</summary>
    public async Task<bool> RestoreAsync()
    {
        var json = await _secure.GetAsync(StorageKey);
        if (string.IsNullOrEmpty(json)) return false;
        try { _session = JsonSerializer.Deserialize<Session>(json); }
        catch (JsonException) { _secure.Remove(StorageKey); return false; }
        if (_session is null) return false;

        var refreshed = await RefreshAsync();
        if (refreshed == AuthOutcome.Success) return true;
        if (refreshed == AuthOutcome.Network) return true; // keep the session; the app works offline and retries later
        await ClearAsync();
        return false;
    }

    public async Task<ProvidersResponse?> LoadProvidersAsync()
    {
        var r = await _api.GetAsync<ProvidersResponse>("/auth/providers", auth: false);
        if (r.Ok) Providers = r.Value;
        return Providers;
    }

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false)
    {
        if (_session is null) return null;
        if (forceRefresh || _session.AccessTokenExpiresAt <= _time.GetUtcNow().AddSeconds(60))
            await RefreshAsync();
        return _session?.AccessToken;
    }

    public async Task<AuthResult> SignInAsGuestAsync(string? displayName)
    {
        var r = await _api.PostAsync<TokenResponse>("/auth/guest", new { displayName });
        return await CompleteAsync(r);
    }

    public bool AppleAvailable => _apple.IsSupported && (Providers?.Apple ?? false);
    public bool GoogleAvailable => _browser.IsSupported && (Providers?.Google ?? false);
    public bool FacebookAvailable => _browser.IsSupported && (Providers?.Facebook ?? false);

    /// <summary>Native Sign in with Apple. With <paramref name="link"/> the identity is added to the current (guest) account.</summary>
    public async Task<AuthResult> SignInWithAppleAsync(bool link = false)
    {
        AppleSignInResult? apple;
        try { apple = await _apple.SignInAsync(); }
        catch (TaskCanceledException) { return new AuthResult(AuthOutcome.Cancelled); }
        if (apple is null) return new AuthResult(AuthOutcome.Cancelled);
        var r = await SendAuthAsync("/auth/apple", new { identityToken = apple.IdentityToken, fullName = apple.FullName, link }, link);
        return await CompleteAsync(r);
    }

    /// <summary>Google or Facebook through the server's OAuth flow in the system browser sheet.</summary>
    public async Task<AuthResult> SignInWithBrowserAsync(string provider, bool link = false)
    {
        var start = $"{_config.ServerUrl.TrimEnd('/')}/auth/oauth/{provider}/start";
        if (link)
        {
            var ticket = await _api.PostAsync<LinkTicketResponse>("/auth/link-ticket", null, auth: true);
            if (!ticket.Ok) return Fail(ticket);
            start += "?link_ticket=" + Uri.EscapeDataString(ticket.Value!.Ticket);
        }

        IReadOnlyDictionary<string, string>? result;
        try { result = await _browser.AuthenticateAsync(new Uri(start), new Uri(AppConfig.AppCallbackUrl)); }
        catch (TaskCanceledException) { return new AuthResult(AuthOutcome.Cancelled); }
        if (result is null) return new AuthResult(AuthOutcome.Cancelled);

        if (result.TryGetValue("error", out var error))
            return error switch
            {
                "cancelled" => new AuthResult(AuthOutcome.Cancelled),
                "already_linked" => new AuthResult(AuthOutcome.AlreadyLinked, "That account is already used by another Ludo Crown player. Sign out and sign in with it instead."),
                _ => new AuthResult(AuthOutcome.Failed, "Sign-in couldn't be completed. Please try again."),
            };
        if (!result.TryGetValue("code", out var code)) return new AuthResult(AuthOutcome.Failed, "Sign-in couldn't be completed.");
        return await CompleteAsync(await _api.PostAsync<TokenResponse>("/auth/exchange", new { code }));
    }

    public async Task<AuthResult> StartEmailAsync(string email)
    {
        var r = await _api.PostAsync<object>("/auth/email/start", new { email });
        return r.Ok ? new AuthResult(AuthOutcome.Success) : Fail(r);
    }

    public async Task<AuthResult> VerifyEmailAsync(string email, string code, bool link = false)
    {
        var r = await SendAuthAsync("/auth/email/verify", new { email, code, link }, link);
        return await CompleteAsync(r);
    }

    public async Task SignOutAsync()
    {
        if (_session is not null)
            await _api.PostAsync<object>("/auth/logout", new { refreshToken = _session.RefreshToken });
        await ClearAsync();
    }

    /// <summary>Deletes the account on the server, then clears local credentials.</summary>
    public async Task<ApiResult<DeleteAccountResponse>> DeleteAccountAsync(string? appleAuthorizationCode)
    {
        var r = await _api.DeleteAsync<DeleteAccountResponse>("/me", new { appleAuthorizationCode });
        if (r.Ok) await ClearAsync();
        return r;
    }

    /// <summary>For account deletion: asks Apple for a fresh authorization code so the server can revoke tokens.</summary>
    public async Task<string?> GetAppleAuthorizationCodeAsync()
    {
        if (!_apple.IsSupported) return null;
        try { return (await _apple.SignInAsync())?.AuthorizationCode; }
        catch (TaskCanceledException) { return null; }
    }

    private Task<ApiResult<TokenResponse>> SendAuthAsync(string path, object body, bool link) =>
        _api.SendAsync<TokenResponse>(HttpMethod.Post, path, body, auth: link);

    private async Task<AuthOutcome> RefreshAsync()
    {
        // Refresh tokens rotate, so concurrent callers must not refresh twice with the same token.
        var tokenBefore = _session?.RefreshToken;
        await _refreshGate.WaitAsync();
        try
        {
            var current = _session;
            if (current is null) return AuthOutcome.Failed;
            if (current.RefreshToken != tokenBefore) return AuthOutcome.Success; // another caller already refreshed
            var r = await _api.PostAsync<TokenResponse>("/auth/refresh", new { refreshToken = current.RefreshToken });
            if (r.Kind == ApiErrorKind.Network) return AuthOutcome.Network;
            if (!r.Ok || r.Value is null) return AuthOutcome.Failed;
            await SaveAsync(r.Value);
            return AuthOutcome.Success;
        }
        finally { _refreshGate.Release(); }
    }

    private async Task<AuthResult> CompleteAsync(ApiResult<TokenResponse> r)
    {
        if (!r.Ok || r.Value is null) return Fail(r);
        await SaveAsync(r.Value);
        return new AuthResult(AuthOutcome.Success);
    }

    private static AuthResult Fail<T>(ApiResult<T> r) => r.Kind switch
    {
        ApiErrorKind.Network => new AuthResult(AuthOutcome.Network, r.Message),
        ApiErrorKind.NotConfigured => new AuthResult(AuthOutcome.NotConfigured, r.Message),
        ApiErrorKind.Conflict => new AuthResult(AuthOutcome.AlreadyLinked, r.Message),
        _ => new AuthResult(AuthOutcome.Failed, r.Message),
    };

    private async Task SaveAsync(TokenResponse t)
    {
        _session = new Session(t.UserId, t.IsGuest, t.AccessToken, t.AccessTokenExpiresAt, t.RefreshToken);
        await _secure.SetAsync(StorageKey, JsonSerializer.Serialize(_session));
        SessionChanged?.Invoke(_session);
    }

    private Task ClearAsync()
    {
        _session = null;
        _secure.Remove(StorageKey);
        SessionChanged?.Invoke(null);
        return Task.CompletedTask;
    }
}
