using System.Data.Common;
using LudoCrown.Core.Model;
using LudoCrown.Server.Data;
using Npgsql;

namespace LudoCrown.Server.Postgres;

/// <summary>Economy, purchases, profile settings and league (PostgreSQL).</summary>
public sealed partial class PostgresStore
{
    private static bool IsCheckViolation(DbException ex) => ex.SqlState == "23514";

    // ---------------- profile extras ----------------

    public async Task<IReadOnlyList<ModeStatsRecord>> GetModeStatsAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await QueryAsync(Sql.GetModeStats, r => new ModeStatsRecord(ParseGameMode(r.GetString(0)), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)), ct, userId);
        return GameModes.All.Select(m => rows.FirstOrDefault(r => r.Mode == m.Mode) ?? ModeStatsRecord.Empty(m.Mode)).ToList();
    }

    private static PrivacySettings MapPrivacy(NpgsqlDataReader r) => new(r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2));

    public async Task<PrivacySettings> GetPrivacyAsync(Guid userId, CancellationToken ct = default) =>
        (await QueryAsync(Sql.GetPrivacy, MapPrivacy, ct, userId)).FirstOrDefault() ?? PrivacySettings.Default;

    public async Task<PrivacySettings> UpdatePrivacyAsync(Guid userId, bool? showStats, bool? showLeague, bool? chatEnabled, CancellationToken ct = default) =>
        (await QueryAsync(Sql.UpsertPrivacy, MapPrivacy, ct, userId, showStats, showLeague, chatEnabled, time.GetUtcNow())).FirstOrDefault() ?? PrivacySettings.Default;

    public Task SetFrameAsync(Guid userId, string? frameId, CancellationToken ct = default) => ExecAsync(Sql.SetFrame, ct, userId, frameId);

    // ---------------- economy ----------------

    public async Task<Balances?> GetBalancesAsync(Guid userId, CancellationToken ct = default)
    {
        var wallet = (await QueryAsync(Sql.GetWallet, r => (Coins: r.GetInt64(0), Gems: r.GetInt64(1)), ct, userId)).ToList();
        if (wallet.Count == 0) return null;
        var items = await QueryAsync(Sql.GetInventory, r => (Id: r.GetString(0), Qty: r.GetInt32(1)), ct, userId);
        return new Balances(wallet[0].Coins, wallet[0].Gems, items.ToDictionary(i => i.Id, i => i.Qty));
    }

    public async Task<EconomyResult> ApplyAsync(EconomyOperation op, CancellationToken ct = default)
    {
        var changes = op.Normalized();
        var coins = changes.Where(c => c.Asset == Economy.Coins).Sum(c => c.Delta);
        var gems = changes.Where(c => c.Asset == Economy.Gems).Sum(c => c.Delta);
        var items = changes.Where(c => c.Asset is not (Economy.Coins or Economy.Gems)).ToList();
        int applied;
        try
        {
            applied = await ScalarAsync<int>(Sql.ApplyEconomy, ct, op.UserId, op.IdempotencyKey, op.Reason, op.MatchId, op.PurchaseId, coins, gems,
                items.Select(i => i.Asset).ToArray(), items.Select(i => (int)i.Delta).ToArray(), op.At);
        }
        catch (DbException ex) when (IsCheckViolation(ex))
        {
            // A balance would go below zero or an item over its limit: the statement changed nothing.
            return new EconomyResult(EconomyOutcome.Insufficient, await GetBalancesAsync(op.UserId, ct) ?? Balances.Empty);
        }
        var balances = await GetBalancesAsync(op.UserId, ct);
        if (balances is null) return new EconomyResult(EconomyOutcome.UnknownUser, Balances.Empty);
        if (applied == 1) return new EconomyResult(EconomyOutcome.Applied, balances);
        return await ScalarAsync<bool>(Sql.OperationExists, ct, op.UserId, op.IdempotencyKey)
            ? new EconomyResult(EconomyOutcome.AlreadyApplied, balances)
            : new EconomyResult(EconomyOutcome.UnknownUser, Balances.Empty);
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(Guid userId, int limit, CancellationToken ct = default) =>
        await QueryAsync(Sql.GetLedger, r => new LedgerEntry(r.GetString(0), r.GetInt64(1), r.GetString(2), Time(r, 3), NullableGuid(r, 4), NullableGuid(r, 5)), ct, userId, limit);

    // ---------------- purchases ----------------

    private static PurchaseStatus ParseStatus(string s) => Enum.Parse<PurchaseStatus>(s, ignoreCase: true);
    private static string StatusText(PurchaseStatus s) => s.ToString().ToLowerInvariant();

    private static PurchaseRecord MapPurchase(NpgsqlDataReader r) => new(r.GetGuid(0), NullableGuid(r, 1), r.GetString(2), r.GetString(3), r.GetString(4), NullableString(r, 5),
        r.GetString(6), ParseStatus(r.GetString(7)), r.GetInt16(8), NullableString(r, 9), NullableTime(r, 10), Time(r, 11), NullableTime(r, 12), NullableTime(r, 13));

    public async Task<PurchaseRecord?> GetPurchaseAsync(string store, string transactionId, CancellationToken ct = default) =>
        (await QueryAsync(Sql.GetPurchase, MapPurchase, ct, store, transactionId)).FirstOrDefault();

    public async Task<(PurchaseRecord Record, bool Created)> CreatePurchaseAsync(PurchaseRecord p, CancellationToken ct = default)
    {
        var inserted = (await QueryAsync(Sql.InsertPurchase, MapPurchase, ct, p.Id, p.UserId, p.Store, p.ProductId, p.TransactionId, p.OriginalTransactionId,
            p.Environment, StatusText(p.Status), (short)p.Quantity, p.FailureReason, p.PurchasedAt, p.CreatedAt)).FirstOrDefault();
        if (inserted is not null) return (inserted, true);
        return ((await GetPurchaseAsync(p.Store, p.TransactionId, ct))!, false);
    }

    public Task UpdatePurchaseAsync(Guid purchaseId, PurchaseStatus status, string? failureReason, DateTimeOffset at, CancellationToken ct = default) =>
        ExecAsync(Sql.UpdatePurchase, ct, purchaseId, StatusText(status), failureReason, at);

    public async Task<IReadOnlyList<PurchaseRecord>> GetPurchasesForUserAsync(Guid userId, int limit, CancellationToken ct = default) =>
        await QueryAsync(Sql.GetPurchasesForUser, MapPurchase, ct, userId, limit);

    // ---------------- league ----------------

    private static SeasonRecord MapSeason(NpgsqlDataReader r) => new(r.GetInt32(0), r.GetInt32(1), Time(r, 2), Time(r, 3),
        Enum.Parse<SeasonStatus>(r.GetString(4), ignoreCase: true), NullableTime(r, 5));

    private static StandingRecord MapStanding(NpgsqlDataReader r) => new(r.GetInt32(0), r.GetGuid(1), r.GetInt64(2), r.GetInt16(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6),
        NullableTime(r, 7), r.IsDBNull(8) ? null : r.GetInt16(8), r.IsDBNull(9) ? null : Enum.Parse<SeasonOutcome>(r.GetString(9), ignoreCase: true));

    private static RewardRecord MapReward(NpgsqlDataReader r) => new(r.GetInt32(0), r.GetGuid(1), r.GetInt16(2), r.GetInt16(3),
        new LeagueReward(r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)), NullableTime(r, 7));

    public async Task<SeasonRecord?> GetCurrentSeasonAsync(CancellationToken ct = default) => (await QueryAsync(Sql.GetCurrentSeason, MapSeason, ct)).FirstOrDefault();
    public async Task<SeasonRecord?> GetLatestSeasonAsync(CancellationToken ct = default) => (await QueryAsync(Sql.GetLatestSeason, MapSeason, ct)).FirstOrDefault();
    public async Task<SeasonRecord?> GetSeasonByNumberAsync(int number, CancellationToken ct = default) => (await QueryAsync(Sql.GetSeasonByNumber, MapSeason, ct, number)).FirstOrDefault();

    public async Task<SeasonRecord> CreateSeasonAsync(int number, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct = default) =>
        (await QueryAsync(Sql.InsertSeason, MapSeason, ct, number, startsAt, endsAt)).FirstOrDefault() ?? (await GetSeasonByNumberAsync(number, ct))!;

    public Task SetSeasonStatusAsync(int seasonId, SeasonStatus status, DateTimeOffset? finalizedAt, CancellationToken ct = default) =>
        ExecAsync(Sql.SetSeasonStatus, ct, seasonId, status.ToString().ToLowerInvariant(), finalizedAt);

    public async Task<int?> GetDivisionAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await QueryAsync(Sql.GetDivision, r => (int)r.GetInt16(0), ct, userId);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<StandingRecord> EnsureStandingAsync(int seasonId, Guid userId, int groupSize, CancellationToken ct = default)
    {
        if (await GetStandingAsync(seasonId, userId, ct) is StandingRecord existing) return existing;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var c = Cmd(conn, tx, Sql.EnsureLeaguePlayer, userId)) await c.ExecuteNonQueryAsync(ct);
        short division;
        await using (var c = Cmd(conn, tx, Sql.GetDivision, userId)) division = (short)(await c.ExecuteScalarAsync(ct))!;

        long groupId;
        await using (var claim = Cmd(conn, tx, Sql.ClaimGroupSeat, seasonId, division, groupSize))
        {
            var claimed = await claim.ExecuteScalarAsync(ct);
            if (claimed is long id) groupId = id;
            else
            {
                await using var create = Cmd(conn, tx, Sql.InsertGroup, seasonId, division);
                groupId = (long)(await create.ExecuteScalarAsync(ct))!;
            }
        }

        StandingRecord? standing;
        await using (var insert = Cmd(conn, tx, Sql.InsertStanding, seasonId, userId, groupId, division))
        await using (var reader = await insert.ExecuteReaderAsync(ct))
            standing = await reader.ReadAsync(ct) ? MapStanding(reader) : null;
        if (standing is null)
        {
            // A concurrent request placed the player first: give the seat back.
            await using var release = Cmd(conn, tx, Sql.ReleaseGroupSeat, groupId);
            await release.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return standing ?? (await GetStandingAsync(seasonId, userId, ct))!;
    }

    public async Task<StandingRecord?> GetStandingAsync(int seasonId, Guid userId, CancellationToken ct = default) =>
        (await QueryAsync(Sql.GetStanding, MapStanding, ct, seasonId, userId)).FirstOrDefault();

    public async Task<IReadOnlyList<StandingEntry>> GetGroupAsync(long groupId, int limit, CancellationToken ct = default) =>
        await QueryAsync(Sql.GetGroup, r => new StandingEntry(r.GetGuid(0), r.GetString(1).Trim(), r.GetString(2), r.GetString(3), NullableString(r, 4),
            r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), NullableTime(r, 8)), ct, groupId, limit);

    public async Task<IReadOnlyList<StandingRecord>> GetSeasonStandingsAsync(int seasonId, CancellationToken ct = default) =>
        await QueryAsync(Sql.GetSeasonStandings, MapStanding, ct, seasonId);

    public async Task SaveSeasonResultsAsync(int seasonId, IReadOnlyList<SeasonResult> results, CancellationToken ct = default)
    {
        var standings = (await GetSeasonStandingsAsync(seasonId, ct)).ToDictionary(s => s.UserId);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var r in results)
        {
            if (!standings.TryGetValue(r.UserId, out var st)) continue;
            await using (var c = Cmd(conn, tx, Sql.SetFinalStanding, seasonId, r.UserId, (short)r.FinalRank, r.Outcome.ToString().ToLowerInvariant())) await c.ExecuteNonQueryAsync(ct);
            await using (var c = Cmd(conn, tx, Sql.UpsertLeagueDivision, r.UserId, (short)r.NextDivision)) await c.ExecuteNonQueryAsync(ct);
            if (r.Reward is { IsEmpty: false } reward)
                await using (var c = Cmd(conn, tx, Sql.InsertReward, seasonId, r.UserId, (short)st.Division, (short)r.FinalRank, reward.Coins, reward.Gems, reward.Shields))
                    await c.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<RewardRecord>> GetUngrantedRewardsAsync(int seasonId, CancellationToken ct = default) =>
        await QueryAsync(Sql.GetUngrantedRewards, MapReward, ct, seasonId);

    public Task MarkRewardGrantedAsync(int seasonId, Guid userId, DateTimeOffset at, CancellationToken ct = default) =>
        ExecAsync(Sql.MarkRewardGranted, ct, seasonId, userId, at);

    public async Task<RewardRecord?> GetRewardAsync(int seasonId, Guid userId, CancellationToken ct = default) =>
        (await QueryAsync(Sql.GetReward, MapReward, ct, seasonId, userId)).FirstOrDefault();
}
