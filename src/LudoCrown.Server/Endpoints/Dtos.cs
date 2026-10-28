using LudoCrown.Server.Auth;
using LudoCrown.Server.Data;

namespace LudoCrown.Server.Endpoints;

public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, Guid UserId, bool IsGuest)
{
    public static TokenResponse From(TokenPair t) => new(t.AccessToken, t.AccessTokenExpiresAt, t.RefreshToken, t.UserId, t.IsGuest);
}

public sealed record ProvidersResponse(bool Guest, bool Apple, bool Google, bool Facebook, bool Email);
public sealed record GuestRequest(string? DisplayName);
public sealed record RefreshRequest(string RefreshToken);
public sealed record AppleSignInRequest(string IdentityToken, string? FullName, bool Link);
public sealed record EmailStartRequest(string Email);
public sealed record EmailVerifyRequest(string Email, string Code, bool Link);
public sealed record ExchangeRequest(string Code);
public sealed record LinkTicketResponse(string Ticket);
public sealed record ApiError(string Code, string Message);

public sealed record MeResponse(
    Guid UserId,
    bool IsGuest,
    string DisplayName,
    string AvatarId,
    string FriendCode,
    int Level,
    int Xp,
    int XpForNextLevel,
    long Coins,
    StatsRecord Stats,
    IReadOnlyList<string> LinkedProviders,
    long Gems = 0);

public sealed record DevGrantRequest(int Amount);
public sealed record GemBalanceResponse(long Gems);

public sealed record UpdateProfileRequest(string? DisplayName, string? AvatarId);
public sealed record DeleteAccountRequest(string? AppleAuthorizationCode);
public sealed record DeleteAccountResponse(bool Deleted, bool AppleTokenRevoked, string? Note);
public sealed record FriendRequest(string FriendCode);
public sealed record FriendRequestResponse(FriendshipStatus Status, string DisplayName);
public sealed record ReportRequest(Guid UserId, Guid? MatchId, string Reason, string? Details);
public sealed record PublicProfileResponse(Guid UserId, string DisplayName, string AvatarId, int Level, int GamesPlayed, int GamesWon);
