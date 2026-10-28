using LudoCrown.Core.Model;

namespace LudoCrown.Server.Data;

/// <summary>
/// The Ludo Crown economy. Every balance (gold coins, gems, inventory items) lives only in the server database and is
/// changed only by the server, through idempotent operations that write a ledger entry per change.
///
/// What each currency is for:
///   * Gold coins — earned in matches, league rewards, or bought in packs. Spent on cosmetics (avatar frames) and Shield bottles.
///   * Gems — earned (starter grant, wins, league) or bought in packs. Spent only on dice rerolls (1 gem each, 7 per Classic
///     match, 5 per Quick match, in every match type including ranked league matches — an explicit product decision).
///   * Shield bottles — inventory items, used in casual matches only (see <see cref="Boosts"/>).
/// There are no loot boxes or randomised paid rewards: every pack lists its exact contents.
/// </summary>
public static class Economy
{
    public const int StarterGems = 10;
    public const int MaxDevGrant = 100;

    public const string Coins = "coins";
    public const string Gems = "gems";

    public static class Reasons
    {
        public const string MatchReward = "match_reward";
        public const string Reroll = "reroll";
        public const string StarterGrant = "starter_grant";
        public const string DevGrant = "dev_grant";
        public const string AdminAdjustment = "admin_adjustment";
        public const string Purchase = "purchase";
        public const string StoreBuy = "store_buy";
        public const string BoostUse = "boost_use";
        public const string LeagueReward = "league_reward";
        public const string Refund = "refund";
    }
}

public enum ItemKind { Boost, Frame }

/// <summary>An inventory item. Frames are cosmetic and owned at most once; boosts stack.</summary>
public sealed record ItemDefinition(string Id, ItemKind Kind, string Name, string Description, int MaxQuantity);

public static class Items
{
    public static readonly ItemDefinition Shield = new(Boosts.ShieldId, ItemKind.Boost, Boosts.ShieldName, Boosts.ShieldEffect + " " + Boosts.ShieldLimits, 999);
    public static readonly ItemDefinition FrameGilded = new("frame_gilded", ItemKind.Frame, "Gilded Frame", "A gold avatar frame shown on your profile and at the table. Cosmetic only.", 1);
    public static readonly ItemDefinition FrameEmerald = new("frame_emerald", ItemKind.Frame, "Emerald Frame", "A green jewelled avatar frame. Cosmetic only.", 1);
    public static readonly ItemDefinition FrameRoyal = new("frame_royal", ItemKind.Frame, "Royal Frame", "A crowned purple avatar frame. Cosmetic only.", 1);

    public static readonly IReadOnlyList<ItemDefinition> All = new[] { Shield, FrameGilded, FrameEmerald, FrameRoyal };

    public static ItemDefinition? Get(string? id) => All.FirstOrDefault(i => i.Id == id);
    public static bool IsFrame(string? id) => Get(id)?.Kind == ItemKind.Frame;
}

/// <summary>One balance change inside an operation: <see cref="Asset"/> is "coins", "gems" or an item id.</summary>
public sealed record AssetChange(string Asset, long Delta);

/// <summary>
/// An all-or-nothing change to one player's balances, applied at most once per <see cref="IdempotencyKey"/>. If any
/// balance would go below zero (or an item above its limit) nothing is applied.
/// </summary>
public sealed record EconomyOperation(Guid UserId, string IdempotencyKey, string Reason, IReadOnlyList<AssetChange> Changes, DateTimeOffset At,
    Guid? MatchId = null, Guid? PurchaseId = null)
{
    /// <summary>Changes with duplicate assets merged and zero deltas dropped.</summary>
    public IReadOnlyList<AssetChange> Normalized() => Changes.GroupBy(c => c.Asset).Select(g => new AssetChange(g.Key, g.Sum(c => c.Delta))).Where(c => c.Delta != 0).ToList();
}

public enum EconomyOutcome { Applied, AlreadyApplied, Insufficient, UnknownUser }

public sealed record Balances(long Coins, long Gems, IReadOnlyDictionary<string, int> Items)
{
    public int Count(string itemId) => Items.GetValueOrDefault(itemId);
    public static Balances Empty { get; } = new(0, 0, new Dictionary<string, int>());
}

public sealed record EconomyResult(EconomyOutcome Outcome, Balances Balances)
{
    public bool Ok => Outcome is EconomyOutcome.Applied or EconomyOutcome.AlreadyApplied;
}

/// <summary>One audited balance change (signed amount) — coins, gems or an item.</summary>
public sealed record LedgerEntry(string Asset, long Delta, string Reason, DateTimeOffset At, Guid? MatchId, Guid? PurchaseId);

public enum GemSpendOutcome { Spent, AlreadySpent, InsufficientGems, UnknownUser }

/// <summary>Result of an idempotent gem spend. <see cref="Balance"/> is the balance after the operation.</summary>
public sealed record GemSpendResult(GemSpendOutcome Outcome, long Balance)
{
    public bool Charged => Outcome is GemSpendOutcome.Spent or GemSpendOutcome.AlreadySpent;
}

// ---------------- purchases ----------------

public static class PurchaseStores
{
    /// <summary>Apple in-app purchase, verified with the App Store Server API.</summary>
    public const string AppStore = "app_store";
    /// <summary>Development-only fake store. The server registers it only in Development with Store:DevSandbox=true.</summary>
    public const string DevSandbox = "dev_sandbox";
}

public enum PurchaseStatus { Pending, Verified, Credited, Rejected, Refunded }

/// <summary>
/// A real-money (or dev sandbox) transaction. Unique per (Store, TransactionId), so one store transaction can be credited
/// once, to one account. <see cref="UserId"/> becomes null if the account is deleted (the transaction record is kept
/// for accounting and refunds, without personal data).
/// </summary>
public sealed record PurchaseRecord(Guid Id, Guid? UserId, string Store, string ProductId, string TransactionId, string? OriginalTransactionId,
    string Environment, PurchaseStatus Status, int Quantity, string? FailureReason, DateTimeOffset? PurchasedAt, DateTimeOffset CreatedAt,
    DateTimeOffset? CreditedAt = null, DateTimeOffset? RefundedAt = null);

// ---------------- profiles ----------------

/// <summary>What a player shows on their public profile, and whether they take part in match chat.</summary>
public sealed record PrivacySettings(bool ShowStats = true, bool ShowLeague = true, bool ChatEnabled = true)
{
    public static PrivacySettings Default { get; } = new();
}

/// <summary>
/// Per-mode statistics from server-recorded results. Rules (also in docs/RULES.md):
///   * Only completed matches recorded by the server count; nothing is ever taken from a client.
///   * A player who leaves or is removed for inactivity/disconnect gets an "abandon": counts as played (so it lowers the
///     win rate) but is shown as an abandon, not a loss; it resets the streak.
///   * A player who disconnects but whose seat is finished by the server without them being removed keeps the real result.
///   * Matches the server voids (every human left; status "abandoned") count only as abandons for those players.
///   * A rematch is a new, independent match.
///   * Win streak = consecutive wins in this mode; any loss or abandon resets it to 0.
/// </summary>
public sealed record ModeStatsRecord(GameMode Mode, int GamesPlayed, int GamesWon, int GamesLost, int Abandons, int CurrentWinStreak, int BestWinStreak)
{
    public int WinRatePercent => GamesPlayed == 0 ? 0 : (int)Math.Round(100.0 * GamesWon / GamesPlayed);
    public static ModeStatsRecord Empty(GameMode mode) => new(mode, 0, 0, 0, 0, 0, 0);
}

// ---------------- league ----------------

public enum SeasonStatus { Active, Finalizing, Completed }

public sealed record SeasonRecord(int Id, int Number, DateTimeOffset StartsAt, DateTimeOffset EndsAt, SeasonStatus Status, DateTimeOffset? FinalizedAt);

public enum SeasonOutcome { Promoted, Stayed, Demoted }

public sealed record StandingRecord(int SeasonId, Guid UserId, long GroupId, int Division, int Points, int MatchesPlayed, int Wins, DateTimeOffset? LastPointsAt,
    int? FinalRank = null, SeasonOutcome? Outcome = null);

/// <summary>A row of a league group table, with public display data only.</summary>
public sealed record StandingEntry(Guid UserId, string PublicId, string DisplayName, string AvatarId, string? FrameId, int Points, int MatchesPlayed, int Wins, DateTimeOffset? LastPointsAt);

public sealed record SeasonResult(Guid UserId, int FinalRank, SeasonOutcome Outcome, int NextDivision, LeagueReward? Reward);

public sealed record LeagueReward(int Coins, int Gems, int Shields)
{
    public bool IsEmpty => Coins == 0 && Gems == 0 && Shields == 0;
    public IReadOnlyList<AssetChange> Changes() => new[]
    {
        new AssetChange(Economy.Coins, Coins), new AssetChange(Economy.Gems, Gems), new AssetChange(Boosts.ShieldId, Shields),
    }.Where(c => c.Delta != 0).ToList();
}

public sealed record RewardRecord(int SeasonId, Guid UserId, int Division, int GroupRank, LeagueReward Reward, DateTimeOffset? GrantedAt);
