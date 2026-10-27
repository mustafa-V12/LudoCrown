using LudoCrown.Server.Auth;
using LudoCrown.Server.Data;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").RequireRateLimiting("auth");

        auth.MapGet("/providers", (IOptions<AuthOptions> o, AppleSignInService apple, OAuthService oauth) =>
            new ProvidersResponse(o.Value.GuestEnabled, apple.Enabled, oauth.Find(AuthProviders.Google) is not null, oauth.Find(AuthProviders.Facebook) is not null, o.Value.Email.Enabled));

        auth.MapPost("/guest", async (GuestRequest req, IOptions<AuthOptions> o, AccountService accounts, CancellationToken ct) =>
        {
            if (!o.Value.GuestEnabled) return Results.NotFound();
            return Results.Ok(TokenResponse.From(await accounts.CreateGuestAsync(req.DisplayName, ct)));
        });

        auth.MapPost("/refresh", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
        {
            var pair = await tokens.RefreshAsync(req.RefreshToken, ct);
            return pair is null ? Results.Unauthorized() : Results.Ok(TokenResponse.From(pair));
        });

        auth.MapPost("/logout", async (RefreshRequest req, TokenService tokens, CancellationToken ct) =>
        {
            await tokens.RevokeAsync(req.RefreshToken, ct);
            return Results.NoContent();
        });

        auth.MapPost("/apple", async (AppleSignInRequest req, HttpContext http, AppleSignInService apple, AccountService accounts, CancellationToken ct) =>
        {
            if (!apple.Enabled) return Error(StatusCodes.Status404NotFound, "provider_disabled", "Sign in with Apple is not configured on this server.");
            var claims = await apple.VerifyAsync(req.IdentityToken, ct);
            if (claims is null) return Error(StatusCodes.Status401Unauthorized, "invalid_token", "Apple sign-in could not be verified.");
            var link = LinkTarget(http, req.Link);
            if (req.Link && link is null) return Results.Unauthorized();
            // Apple's relay email is not needed for gameplay, so it is not stored.
            var result = await accounts.SignInWithIdentityAsync(AuthProviders.Apple, claims.Subject, null, req.FullName, link, ct);
            return ToResult(result);
        });

        auth.MapPost("/email/start", async (EmailStartRequest req, EmailCodeService email, CancellationToken ct) =>
            await email.StartAsync(req.Email, ct) switch
            {
                EmailCodeResult.Sent => Results.Accepted(),
                EmailCodeResult.InvalidEmail => Error(StatusCodes.Status400BadRequest, "invalid_email", "Enter a valid email address."),
                EmailCodeResult.TooSoon => Error(StatusCodes.Status429TooManyRequests, "too_soon", "A code was just sent. Please wait a moment before asking again."),
                _ => Error(StatusCodes.Status404NotFound, "provider_disabled", "Email sign-in is not configured on this server."),
            });

        auth.MapPost("/email/verify", async (EmailVerifyRequest req, HttpContext http, EmailCodeService email, AccountService accounts, CancellationToken ct) =>
        {
            var (result, address) = await email.VerifyAsync(req.Email, req.Code, ct);
            if (result == EmailVerifyResult.TooManyAttempts) return Error(StatusCodes.Status429TooManyRequests, "too_many_attempts", "Too many attempts. Request a new code.");
            if (result != EmailVerifyResult.Ok) return Error(StatusCodes.Status401Unauthorized, "invalid_code", "That code is wrong or has expired.");
            var link = LinkTarget(http, req.Link);
            if (req.Link && link is null) return Results.Unauthorized();
            return ToResult(await accounts.SignInWithIdentityAsync(AuthProviders.Email, address!, address, null, link, ct));
        });

        auth.MapGet("/oauth/{provider}/start", async (string provider, string? link_ticket, OAuthService oauth, CancellationToken ct) =>
        {
            var url = await oauth.StartAsync(provider, link_ticket, ct);
            return url is null ? Results.NotFound() : Results.Redirect(url);
        });

        auth.MapGet("/oauth/{provider}/callback", async (string provider, string? code, string? state, string? error, OAuthService oauth, CancellationToken ct) =>
            Results.Redirect(await oauth.CallbackAsync(provider, code, state, error, ct)));

        auth.MapPost("/exchange", async (ExchangeRequest req, OAuthService oauth, CancellationToken ct) =>
        {
            var pair = await oauth.ExchangeLoginCodeAsync(req.Code, ct);
            return pair is null ? Results.Unauthorized() : Results.Ok(TokenResponse.From(pair));
        });

        auth.MapPost("/link-ticket", async (HttpContext http, OAuthService oauth, CancellationToken ct) =>
            Results.Ok(new LinkTicketResponse(await oauth.CreateLinkTicketAsync(http.User.UserId(), ct))))
            .RequireAuthorization();
    }

    private static Guid? LinkTarget(HttpContext http, bool link) =>
        link && http.User.Identity?.IsAuthenticated == true ? http.User.UserId() : null;

    private static IResult ToResult(SignInResult result) => result.Outcome switch
    {
        SignInOutcome.IdentityBelongsToAnotherAccount => Error(StatusCodes.Status409Conflict, "already_linked",
            "That sign-in is already used by another Ludo Crown account. Sign out and sign in with it instead."),
        SignInOutcome.UserNotFound => Results.Unauthorized(),
        _ => Results.Ok(TokenResponse.From(result.Tokens!)),
    };

    internal static IResult Error(int status, string code, string message) => Results.Json(new ApiError(code, message), statusCode: status);
}
