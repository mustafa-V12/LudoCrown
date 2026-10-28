using LudoCrown.Core.Protocol;
using LudoCrown.Server.Auth;
using LudoCrown.Server.Data;
using LudoCrown.Server.Matches;

namespace LudoCrown.Server.Endpoints;

/// <summary>Profile, stats, history, social and account-management endpoints. Every route acts only on the caller's own data.</summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var me = app.MapGroup("/me").RequireAuthorization().RequireRateLimiting("api");

        me.MapGet("", async (HttpContext http, IAccountStore accounts, IProfileStore profiles, IWalletStore wallet, CancellationToken ct) =>
        {
            var id = http.User.UserId();
            var user = await accounts.GetUserAsync(id, ct);
            var profile = await profiles.GetProfileAsync(id, ct);
            if (user is null || profile is null) return Results.Unauthorized();
            var identities = await accounts.GetIdentitiesAsync(id, ct);
            return Results.Ok(new MeResponse(id, user.IsGuest, profile.DisplayName, profile.AvatarId, profile.FriendCode, profile.Level, profile.Xp,
                profile.Level * Progression.XpPerLevel, await profiles.GetCoinsAsync(id, ct), await profiles.GetStatsAsync(id, ct),
                identities.Select(i => i.Provider).Where(p => p != AuthProviders.Guest).Distinct().ToList(),
                await wallet.GetGemsAsync(id, ct)));
        });

        me.MapPatch("/profile", async (UpdateProfileRequest req, HttpContext http, IProfileStore profiles, TimeProvider time, CancellationToken ct) =>
        {
            if (req.DisplayName is not null && DisplayNames.Validate(req.DisplayName) is string reason)
                return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_name", reason);
            if (req.AvatarId is not null && !Avatars.IsValid(req.AvatarId))
                return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_avatar", "Unknown avatar.");
            var p = await profiles.UpdateProfileAsync(http.User.UserId(), req.DisplayName?.Trim(), req.AvatarId, time.GetUtcNow(), ct);
            return Results.Ok(new { p.DisplayName, p.AvatarId });
        });

        me.MapGet("/gems", async (HttpContext http, IWalletStore wallet, CancellationToken ct) =>
            Results.Ok(new GemBalanceResponse(await wallet.GetGemsAsync(http.User.UserId(), ct))));

        // Paged: pass the StartedAt of the last item as ?before= to get the next page. At most 50 per page.
        me.MapGet("/matches", async (int? limit, DateTimeOffset? before, HttpContext http, IMatchStore matches, CancellationToken ct) =>
            Results.Ok(await matches.GetHistoryAsync(http.User.UserId(), Math.Clamp(limit ?? 20, 1, 50), before, ct)));

        me.MapGet("/export", async (HttpContext http, AccountService accounts, CancellationToken ct) =>
        {
            var export = await accounts.ExportAsync(http.User.UserId(), ct);
            if (export is null) return Results.NotFound();
            http.Response.Headers.ContentDisposition = "attachment; filename=ludocrown-data.json";
            return Results.Ok(export);
        });

        me.MapDelete("", async ([Microsoft.AspNetCore.Mvc.FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] DeleteAccountRequest? req, HttpContext http, IAccountStore accounts, AccountService accountService,
            AppleSignInService apple, MatchService matches, MatchmakingService matchmaking, CancellationToken ct) =>
        {
            var id = http.User.UserId();
            var identities = await accounts.GetIdentitiesAsync(id, ct);
            var revoked = false;
            string? note = null;
            if (identities.Any(i => i.Provider == AuthProviders.Apple))
            {
                if (!string.IsNullOrEmpty(req?.AppleAuthorizationCode)) revoked = await apple.RevokeAsync(req.AppleAuthorizationCode, ct);
                if (!revoked) note = "Your account was deleted. You can also remove Ludo Crown from Settings > Apple ID > Sign in with Apple.";
            }
            await matchmaking.CancelAsync(id);
            await matches.OnAccountDeletedAsync(id);
            await accountService.DeleteAccountAsync(id, ct);
            return Results.Ok(new DeleteAccountResponse(true, revoked, note));
        });

        var friends = app.MapGroup("/friends").RequireAuthorization().RequireRateLimiting("api");

        friends.MapGet("", async (HttpContext http, ISocialStore social, CancellationToken ct) =>
            Results.Ok(await social.GetFriendsAsync(http.User.UserId(), ct)));

        friends.MapPost("/requests", async (FriendRequest req, HttpContext http, IProfileStore profiles, ISocialStore social, TimeProvider time, CancellationToken ct) =>
        {
            var id = http.User.UserId();
            var code = req.FriendCode?.Trim().ToUpperInvariant() ?? "";
            var target = code.Length == 8 ? await profiles.GetProfileByFriendCodeAsync(code, ct) : null;
            if (target is null || target.UserId == id || await social.IsBlockedEitherWayAsync(id, target.UserId, ct))
                return AuthEndpoints.Error(StatusCodes.Status404NotFound, "not_found", "No player with that friend code.");
            var status = await social.RequestFriendAsync(id, target.UserId, time.GetUtcNow(), ct);
            return Results.Ok(new FriendRequestResponse(status, target.DisplayName));
        });

        friends.MapPost("/{userId:guid}/accept", async (Guid userId, HttpContext http, ISocialStore social, CancellationToken ct) =>
            await social.AcceptFriendAsync(http.User.UserId(), userId, ct) ? Results.NoContent() : Results.NotFound());

        friends.MapDelete("/{userId:guid}", async (Guid userId, HttpContext http, ISocialStore social, CancellationToken ct) =>
        {
            await social.RemoveFriendAsync(http.User.UserId(), userId, ct);
            return Results.NoContent();
        });

        var blocks = app.MapGroup("/blocks").RequireAuthorization().RequireRateLimiting("api");

        blocks.MapGet("", async (HttpContext http, ISocialStore social, IProfileStore profiles, CancellationToken ct) =>
        {
            var ids = await social.GetBlockedAsync(http.User.UserId(), ct);
            var map = await profiles.GetProfilesAsync(ids, ct);
            return Results.Ok(ids.Select(i => new { UserId = i, DisplayName = map.GetValueOrDefault(i)?.DisplayName ?? "Player" }));
        });

        blocks.MapPost("/{userId:guid}", async (Guid userId, HttpContext http, ISocialStore social, TimeProvider time, CancellationToken ct) =>
        {
            var id = http.User.UserId();
            if (userId == id) return Results.BadRequest();
            await social.BlockAsync(id, userId, time.GetUtcNow(), ct);
            return Results.NoContent();
        });

        blocks.MapDelete("/{userId:guid}", async (Guid userId, HttpContext http, ISocialStore social, CancellationToken ct) =>
        {
            await social.UnblockAsync(http.User.UserId(), userId, ct);
            return Results.NoContent();
        });

        app.MapPost("/reports", async (ReportRequest req, HttpContext http, ISocialStore social, IAccountStore accounts, IProfileStore profiles, TimeProvider time, CancellationToken ct) =>
        {
            var id = http.User.UserId();
            if (!ReportReasons.All.Contains(req.Reason)) return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_reason", "Choose a reason.");
            // Players are identified by public id (friend code) in the app; the internal id is still accepted for older clients.
            Guid? target = req.PublicId is { Length: 8 } code ? (await profiles.GetProfileByFriendCodeAsync(code.ToUpperInvariant(), ct))?.UserId : req.UserId;
            if (target is not Guid reported || reported == id || await accounts.GetUserAsync(reported, ct) is null) return Results.NotFound();
            var details = req.Details?.Trim();
            if (details is { Length: > 500 }) details = details[..500];
            await social.AddReportAsync(new ReportRecord(Guid.NewGuid(), id, reported, req.MatchId, req.Reason, details, time.GetUtcNow()), ct);
            return Results.Accepted();
        }).RequireAuthorization().RequireRateLimiting("api");

        // Block / unblock by public id (from a profile card at the table, in results, or in a league table).
        app.MapPost("/players/{publicId}/block", async (string publicId, HttpContext http, IProfileStore profiles, ISocialStore social, TimeProvider time, CancellationToken ct) =>
        {
            var id = http.User.UserId();
            var p = await profiles.GetProfileByFriendCodeAsync(publicId.Trim().ToUpperInvariant(), ct);
            if (p is null || p.UserId == id) return Results.NotFound();
            await social.BlockAsync(id, p.UserId, time.GetUtcNow(), ct);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("api");
    }

    /// <summary>
    /// DEVELOPMENT ONLY. Lets a signed-in tester add gems to their OWN account to try rerolls. Mapped only when the server
    /// runs in the Development environment with DevTools:AllowGemGrant=true (see Program.cs); never in production.
    /// </summary>
    public static void MapDevEndpoints(this WebApplication app)
    {
        app.MapPost("/dev/gems/grant", async (DevGrantRequest req, HttpContext http, IWalletStore wallet, TimeProvider time, CancellationToken ct) =>
        {
            if (req.Amount is < 1 or > Economy.MaxDevGrant)
                return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_amount", $"Grant between 1 and {Economy.MaxDevGrant} gems.");
            var balance = await wallet.GrantGemsAsync(http.User.UserId(), req.Amount, Economy.Reasons.DevGrant, $"dev:{Guid.NewGuid():N}", time.GetUtcNow(), ct);
            return Results.Ok(new GemBalanceResponse(balance));
        }).RequireAuthorization().RequireRateLimiting("api");
    }
}
