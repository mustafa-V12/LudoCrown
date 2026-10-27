using System.Security.Cryptography;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Data;

namespace LudoCrown.Server.Auth;

public enum SignInOutcome { SignedIn, Created, Linked, IdentityBelongsToAnotherAccount, UserNotFound }

public sealed record SignInResult(SignInOutcome Outcome, TokenPair? Tokens);

/// <summary>Account lifecycle: guest creation, provider sign-in/linking, deletion and export.</summary>
public sealed class AccountService(
    IAccountStore accounts,
    IProfileStore profiles,
    IMatchStore matches,
    ISocialStore social,
    TokenService tokens)
{
    public async Task<TokenPair> CreateGuestAsync(string? displayName, CancellationToken ct = default)
    {
        var user = await accounts.CreateUserAsync(true, SanitizeName(displayName), RandomAvatar(), ct);
        await accounts.AddIdentityAsync(user.Id, AuthProviders.Guest, user.Id.ToString(), null, ct);
        return await tokens.IssueAsync(user.Id, true, ct);
    }

    /// <summary>
    /// Signs in with a verified provider identity. When <paramref name="linkToUserId"/> is set, the identity is
    /// attached to that (usually guest) account instead, upgrading it to a full account and keeping progress.
    /// </summary>
    public async Task<SignInResult> SignInWithIdentityAsync(string provider, string subject, string? email, string? suggestedName, Guid? linkToUserId, CancellationToken ct = default)
    {
        if (linkToUserId is Guid linkId)
        {
            var user = await accounts.GetUserAsync(linkId, ct);
            if (user is null) return new SignInResult(SignInOutcome.UserNotFound, null);
            if (!await accounts.AddIdentityAsync(linkId, provider, subject, email, ct))
                return new SignInResult(SignInOutcome.IdentityBelongsToAnotherAccount, null);
            await accounts.SetGuestAsync(linkId, false, ct);
            return new SignInResult(SignInOutcome.Linked, await tokens.IssueAsync(linkId, false, ct));
        }

        var existing = await accounts.FindUserByIdentityAsync(provider, subject, ct);
        if (existing is Guid id)
        {
            var user = await accounts.GetUserAsync(id, ct);
            if (user is not null) return new SignInResult(SignInOutcome.SignedIn, await tokens.IssueAsync(id, user.IsGuest, ct));
        }

        var created = await accounts.CreateUserAsync(false, SanitizeName(suggestedName), RandomAvatar(), ct);
        await accounts.AddIdentityAsync(created.Id, provider, subject, email, ct);
        return new SignInResult(SignInOutcome.Created, await tokens.IssueAsync(created.Id, false, ct));
    }

    public async Task DeleteAccountAsync(Guid userId, CancellationToken ct = default)
    {
        await accounts.RevokeAllRefreshTokensAsync(userId, DateTimeOffset.UtcNow, ct);
        await accounts.DeleteUserAsync(userId, ct);
    }

    public async Task<AccountExport?> ExportAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await accounts.GetUserAsync(userId, ct);
        if (user is null) return null;
        return new AccountExport(
            user,
            await accounts.GetIdentitiesAsync(userId, ct),
            await profiles.GetProfileAsync(userId, ct),
            await profiles.GetStatsAsync(userId, ct),
            await profiles.GetCoinsAsync(userId, ct),
            await matches.GetHistoryAsync(userId, 1000, ct),
            await social.GetFriendsAsync(userId, ct),
            await social.GetBlockedAsync(userId, ct));
    }

    /// <summary>Turns a provider-supplied or user-typed name into a valid display name, or generates one.</summary>
    public static string SanitizeName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var cleaned = new string(name.Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '.' or '-').ToArray());
            while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
            if (cleaned.Length > DisplayNames.MaxLength) cleaned = cleaned[..DisplayNames.MaxLength].Trim();
            if (DisplayNames.Validate(cleaned) is null) return cleaned;
        }
        return $"Player{RandomNumberGenerator.GetInt32(1000, 10000)}";
    }

    private static string RandomAvatar() => Avatars.All[RandomNumberGenerator.GetInt32(Avatars.All.Count)];
}
