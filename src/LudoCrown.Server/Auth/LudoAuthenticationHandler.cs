using System.Security.Claims;
using System.Text.Encodings.Web;
using LudoCrown.Core.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Auth;

/// <summary>
/// Authenticates requests with the server's access token: "Authorization: Bearer ..." for HTTP, and the
/// "access_token" query parameter for the SignalR hub (WebSockets cannot send headers from browsers/apps).
/// </summary>
public sealed class LudoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LudoBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = null;
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = header["Bearer ".Length..].Trim();
        else if (Request.Path.StartsWithSegments(HubNames.Path)) token = Request.Query["access_token"];

        if (string.IsNullOrEmpty(token)) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = tokens.ValidateAccessToken(token, SchemeName);
        return Task.FromResult(principal is null
            ? AuthenticateResult.Fail("Invalid or expired token")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Not authenticated."));

    public static bool IsGuest(this ClaimsPrincipal user) => user.FindFirstValue(TokenService.GuestClaim) == "true";
}
