namespace LudoCrown.Server.Data;

public static class AuthProviders
{
    public const string Guest = "guest";
    public const string Apple = "apple";
    public const string Google = "google";
    public const string Facebook = "facebook";
    public const string Email = "email";

    public static readonly IReadOnlyList<string> All = new[] { Guest, Apple, Google, Facebook, Email };
}

public sealed record UserRecord(Guid Id, bool IsGuest, DateTimeOffset CreatedAt);

/// <summary>A sign-in method linked to an account. Subject is the provider's stable user id (never a password).</summary>
public sealed record IdentityRecord(Guid UserId, string Provider, string Subject, string? Email, DateTimeOffset CreatedAt);

/// <summary><see cref="FrameId"/> is the equipped avatar frame (a cosmetic the player owns), or null.</summary>
public sealed record ProfileRecord(Guid UserId, string DisplayName, string AvatarId, string FriendCode, int Xp, DateTimeOffset UpdatedAt, string? FrameId = null)
{
    public int Level => Progression.LevelForXp(Xp);
}

public sealed record StatsRecord(Guid UserId, int GamesPlayed, int GamesWon, int TwoPlayerWins, int FourPlayerWins, int PiecesCaptured, int Abandons, int CurrentWinStreak, int BestWinStreak)
{
    public static StatsRecord Empty(Guid userId) => new(userId, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record RefreshTokenRecord(Guid Id, Guid UserId, byte[] TokenHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, Guid? ReplacedBy);

public static class AuthCodeKinds
{
    public const string EmailOtp = "email_otp";
    public const string OAuthState = "oauth_state";
    public const string LoginCode = "login_code";
    public const string LinkTicket = "link_ticket";
}

/// <summary>Short-lived single-use secret (email code, OAuth state, login hand-off). Only the hash is stored.</summary>
public sealed record AuthCodeRecord(Guid Id, string Kind, byte[] CodeHash, Guid? UserId, string? Payload, DateTimeOffset ExpiresAt, int Attempts);

/// <summary>How a match was formed. Matchmaking = ranked queue (league points, no boosts); Casual = casual queue (boosts, no league); Private = private table (boosts, no league, no coins).</summary>
public enum MatchMode { Matchmaking, Private, Casual }
public enum MatchStatus { Active, Completed, Abandoned }
public enum ParticipantResult { Win, Loss, Abandoned }

public sealed record MatchParticipantRecord(int Seat, Guid? UserId, bool IsBot, string DisplayName, string Color);

public sealed record MatchRecord(Guid Id, MatchMode Mode, int PlayerCount, string? RoomCode, DateTimeOffset StartedAt, IReadOnlyList<MatchParticipantRecord> Participants,
    LudoCrown.Core.Model.GameMode GameMode = LudoCrown.Core.Model.GameMode.Classic);

/// <summary><see cref="LeaguePoints"/> is null when the match does not count for the league.</summary>
public sealed record ParticipantOutcome(int Seat, Guid? UserId, int Placement, ParticipantResult Result, int CoinsAwarded, int XpAwarded, int PiecesCaptured,
    int GemsAwarded = 0, int RerollsUsed = 0, int BoostsUsed = 0, int? LeaguePoints = null);

/// <summary>
/// The authoritative result. <see cref="GameMode"/> drives per-mode stats; <see cref="LeagueSeasonId"/> (set only for ranked
/// matches with 2+ humans) says which season standings receive the outcomes' league points.
/// </summary>
public sealed record MatchCompletion(Guid MatchId, MatchStatus Status, DateTimeOffset EndedAt, Guid? WinnerUserId, string FinalStateJson, IReadOnlyList<ParticipantOutcome> Outcomes, int PlayerCount,
    LudoCrown.Core.Model.GameMode GameMode = LudoCrown.Core.Model.GameMode.Classic, int? LeagueSeasonId = null);

public sealed record ActiveMatchRecord(MatchRecord Match, string StateJson);

public sealed record MatchHistoryItem(Guid MatchId, MatchMode Mode, int PlayerCount, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int Placement, ParticipantResult Result, int CoinsAwarded, int XpAwarded, IReadOnlyList<string> Opponents,
    LudoCrown.Core.Model.GameMode GameMode = LudoCrown.Core.Model.GameMode.Classic, int RerollsUsed = 0, int GemsAwarded = 0, int? LeaguePoints = null);

public enum FriendshipStatus { Pending, Accepted }

public sealed record FriendRecord(Guid UserId, string DisplayName, string AvatarId, int Level, FriendshipStatus Status, bool IncomingRequest, string FriendCode = "");

public static class ReportReasons
{
    public static readonly IReadOnlyList<string> All = new[] { "offensive_name", "cheating", "harassment", "other" };
}

public sealed record ReportRecord(Guid Id, Guid ReporterId, Guid ReportedId, Guid? MatchId, string Reason, string? Details, DateTimeOffset CreatedAt);

public sealed record AccountExport(UserRecord User, IReadOnlyList<IdentityRecord> Identities, ProfileRecord? Profile, StatsRecord Stats, long Coins, IReadOnlyList<MatchHistoryItem> Matches, IReadOnlyList<FriendRecord> Friends, IReadOnlyList<Guid> Blocked,
    long Gems = 0, IReadOnlyDictionary<string, int>? Inventory = null, IReadOnlyList<LedgerEntry>? Ledger = null, IReadOnlyList<PurchaseRecord>? Purchases = null,
    PrivacySettings? Privacy = null, IReadOnlyList<ModeStatsRecord>? ModeStats = null);

public static class Progression
{
    public const int XpPerLevel = 250;
    public static int LevelForXp(int xp) => 1 + Math.Max(0, xp) / XpPerLevel;
}
