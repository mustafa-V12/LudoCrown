namespace LudoCrown.Server.Data;

/// <summary>Accounts, sign-in identities, refresh tokens and one-time auth codes.</summary>
public interface IAccountStore
{
    /// <summary>Creates the user, their profile, stats and wallet rows atomically.</summary>
    Task<UserRecord> CreateUserAsync(bool isGuest, string displayName, string avatarId, CancellationToken ct = default);
    Task<UserRecord?> GetUserAsync(Guid userId, CancellationToken ct = default);
    Task<Guid?> FindUserByIdentityAsync(string provider, string subject, CancellationToken ct = default);
    /// <summary>Links an identity. Returns false if it already belongs to a different account.</summary>
    Task<bool> AddIdentityAsync(Guid userId, string provider, string subject, string? email, CancellationToken ct = default);
    Task<IReadOnlyList<IdentityRecord>> GetIdentitiesAsync(Guid userId, CancellationToken ct = default);
    Task SetGuestAsync(Guid userId, bool isGuest, CancellationToken ct = default);
    /// <summary>Deletes personal data. Match records keep an anonymised seat so opponents' history stays consistent.</summary>
    Task DeleteUserAsync(Guid userId, CancellationToken ct = default);

    Task StoreRefreshTokenAsync(RefreshTokenRecord token, CancellationToken ct = default);
    Task<RefreshTokenRecord?> GetRefreshTokenAsync(byte[] tokenHash, CancellationToken ct = default);
    Task RevokeRefreshTokenAsync(Guid tokenId, Guid? replacedBy, DateTimeOffset at, CancellationToken ct = default);
    Task RevokeAllRefreshTokensAsync(Guid userId, DateTimeOffset at, CancellationToken ct = default);

    Task CreateAuthCodeAsync(AuthCodeRecord code, CancellationToken ct = default);
    /// <summary>Atomically consumes an unexpired, unconsumed code. Returns null if missing, expired or used.</summary>
    Task<AuthCodeRecord?> ConsumeAuthCodeAsync(string kind, byte[] codeHash, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Latest live code of a kind for a payload key (e.g. email address), for attempt counting.</summary>
    Task<AuthCodeRecord?> FindLiveAuthCodeAsync(string kind, string payload, DateTimeOffset now, CancellationToken ct = default);
    Task<int> IncrementAuthCodeAttemptsAsync(Guid codeId, CancellationToken ct = default);
    Task InvalidateAuthCodesAsync(string kind, string payload, CancellationToken ct = default);
}

public interface IProfileStore
{
    Task<ProfileRecord?> GetProfileAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, ProfileRecord>> GetProfilesAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);
    Task<ProfileRecord?> GetProfileByFriendCodeAsync(string friendCode, CancellationToken ct = default);
    Task<ProfileRecord> UpdateProfileAsync(Guid userId, string? displayName, string? avatarId, DateTimeOffset at, CancellationToken ct = default);
    Task<StatsRecord> GetStatsAsync(Guid userId, CancellationToken ct = default);
    Task<long> GetCoinsAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Gem balances. All changes are atomic and recorded in the ledger.</summary>
public interface IWalletStore
{
    Task<long> GetGemsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Spends <paramref name="cost"/> gems exactly once per <paramref name="idempotencyKey"/>. Retrying with the same key
    /// returns <see cref="GemSpendOutcome.AlreadySpent"/> without charging again. Never lets a balance go negative.
    /// </summary>
    Task<GemSpendResult> SpendGemsAsync(Guid userId, Guid? matchId, string idempotencyKey, int cost, string reason, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Adds gems (starter grant, dev grant). Idempotent per key. Returns the new balance.</summary>
    Task<long> GrantGemsAsync(Guid userId, int amount, string reason, string idempotencyKey, DateTimeOffset at, CancellationToken ct = default);
}

public interface IMatchStore
{
    Task CreateMatchAsync(MatchRecord match, string initialStateJson, CancellationToken ct = default);
    Task SaveLiveStateAsync(Guid matchId, string stateJson, long version, CancellationToken ct = default);
    /// <summary>
    /// Records the result and applies stats, XP and coin rewards in one transaction. Idempotent:
    /// completing an already-completed match does nothing and returns false.
    /// </summary>
    Task<bool> CompleteMatchAsync(MatchCompletion completion, CancellationToken ct = default);
    Task<IReadOnlyList<ActiveMatchRecord>> GetActiveMatchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MatchHistoryItem>> GetHistoryAsync(Guid userId, int limit, CancellationToken ct = default);
}

public interface ISocialStore
{
    Task<IReadOnlyList<FriendRecord>> GetFriendsAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Creates a request, or accepts it if the other user already asked. Returns the resulting status.</summary>
    Task<FriendshipStatus> RequestFriendAsync(Guid fromUser, Guid toUser, DateTimeOffset at, CancellationToken ct = default);
    Task<bool> AcceptFriendAsync(Guid userId, Guid requesterId, CancellationToken ct = default);
    Task RemoveFriendAsync(Guid userId, Guid otherId, CancellationToken ct = default);
    Task BlockAsync(Guid blockerId, Guid blockedId, DateTimeOffset at, CancellationToken ct = default);
    Task UnblockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetBlockedAsync(Guid userId, CancellationToken ct = default);
    Task<bool> IsBlockedEitherWayAsync(Guid a, Guid b, CancellationToken ct = default);
    Task AddReportAsync(ReportRecord report, CancellationToken ct = default);
}
