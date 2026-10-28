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
    /// <summary>Classic and Quick statistics (one entry per available mode, zeros when unplayed).</summary>
    Task<IReadOnlyList<ModeStatsRecord>> GetModeStatsAsync(Guid userId, CancellationToken ct = default);
    Task<PrivacySettings> GetPrivacyAsync(Guid userId, CancellationToken ct = default);
    Task<PrivacySettings> UpdatePrivacyAsync(Guid userId, bool? showStats, bool? showLeague, bool? chatEnabled, CancellationToken ct = default);
    /// <summary>Equips a frame (ownership must be checked by the caller) or clears it with null.</summary>
    Task SetFrameAsync(Guid userId, string? frameId, CancellationToken ct = default);
}

/// <summary>
/// Coins, gems and inventory. <see cref="ApplyAsync"/> is the only way to change them: all-or-nothing, never below zero,
/// at most once per idempotency key, with a ledger entry for every change.
/// </summary>
public interface IEconomyStore
{
    /// <summary>Null if the user does not exist.</summary>
    Task<Balances?> GetBalancesAsync(Guid userId, CancellationToken ct = default);
    Task<EconomyResult> ApplyAsync(EconomyOperation operation, CancellationToken ct = default);
    /// <summary>Most recent ledger entries first (bounded).</summary>
    Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(Guid userId, int limit, CancellationToken ct = default);
}

/// <summary>Real-money and sandbox purchase records. Crediting goes through <see cref="IEconomyStore"/>.</summary>
public interface IPurchaseStore
{
    Task<PurchaseRecord?> GetPurchaseAsync(string store, string transactionId, CancellationToken ct = default);
    /// <summary>Inserts the record unless (store, transaction id) exists. Returns the stored record and whether it was created.</summary>
    Task<(PurchaseRecord Record, bool Created)> CreatePurchaseAsync(PurchaseRecord record, CancellationToken ct = default);
    Task UpdatePurchaseAsync(Guid purchaseId, PurchaseStatus status, string? failureReason, DateTimeOffset at, CancellationToken ct = default);
    Task<IReadOnlyList<PurchaseRecord>> GetPurchasesForUserAsync(Guid userId, int limit, CancellationToken ct = default);
}

/// <summary>League seasons, groups, standings and season rewards.</summary>
public interface ILeagueStore
{
    /// <summary>The oldest season that is not completed (active, or finalizing after an interruption), or null.</summary>
    Task<SeasonRecord?> GetCurrentSeasonAsync(CancellationToken ct = default);
    Task<SeasonRecord?> GetLatestSeasonAsync(CancellationToken ct = default);
    Task<SeasonRecord?> GetSeasonByNumberAsync(int number, CancellationToken ct = default);
    /// <summary>Creates season <paramref name="number"/> unless it exists; returns the stored season.</summary>
    Task<SeasonRecord> CreateSeasonAsync(int number, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct = default);
    Task SetSeasonStatusAsync(int seasonId, SeasonStatus status, DateTimeOffset? finalizedAt, CancellationToken ct = default);
    /// <summary>The player's division carried between seasons (0 = lowest) if they have one.</summary>
    Task<int?> GetDivisionAsync(Guid userId, CancellationToken ct = default);
    /// <summary>Puts the player in a group of <paramref name="groupSize"/> for their division, unless already placed. Idempotent.</summary>
    Task<StandingRecord> EnsureStandingAsync(int seasonId, Guid userId, int groupSize, CancellationToken ct = default);
    Task<StandingRecord?> GetStandingAsync(int seasonId, Guid userId, CancellationToken ct = default);
    /// <summary>A group's table, best first (points, wins, earliest to reach the points), bounded by <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<StandingEntry>> GetGroupAsync(long groupId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<StandingRecord>> GetSeasonStandingsAsync(int seasonId, CancellationToken ct = default);
    /// <summary>Saves final ranks/outcomes, next-season divisions and reward rows. Idempotent (rewards are inserted once).</summary>
    Task SaveSeasonResultsAsync(int seasonId, IReadOnlyList<SeasonResult> results, CancellationToken ct = default);
    Task<IReadOnlyList<RewardRecord>> GetUngrantedRewardsAsync(int seasonId, CancellationToken ct = default);
    Task MarkRewardGrantedAsync(int seasonId, Guid userId, DateTimeOffset at, CancellationToken ct = default);
    Task<RewardRecord?> GetRewardAsync(int seasonId, Guid userId, CancellationToken ct = default);
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
    /// Records the result and applies stats (overall and per mode), XP, coin/gem rewards and league points in one transaction. Idempotent:
    /// completing an already-completed match does nothing and returns false.
    /// </summary>
    Task<bool> CompleteMatchAsync(MatchCompletion completion, CancellationToken ct = default);
    Task<IReadOnlyList<ActiveMatchRecord>> GetActiveMatchesAsync(CancellationToken ct = default);
    /// <summary>Newest first, bounded by <paramref name="limit"/>; pass <paramref name="before"/> (a StartedAt) for the next page.</summary>
    Task<IReadOnlyList<MatchHistoryItem>> GetHistoryAsync(Guid userId, int limit, DateTimeOffset? before = null, CancellationToken ct = default);
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
