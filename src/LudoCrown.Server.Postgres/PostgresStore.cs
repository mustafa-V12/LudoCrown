using LudoCrown.Server.Data;
using Npgsql;

namespace LudoCrown.Server.Postgres;

/// <summary>
/// Production store. All statements come from <see cref="Sql"/>. Multi-step writes (account creation,
/// match completion, friend requests) run in a single transaction so data stays consistent.
/// </summary>
public sealed class PostgresStore(NpgsqlDataSource db, TimeProvider time) : IAccountStore, IProfileStore, IMatchStore, ISocialStore, IWalletStore
{
    // ---------------- helpers ----------------

    private static NpgsqlCommand Cmd(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params object?[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = Normalize(a) });
        return cmd;
    }

    private static object Normalize(object? value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset d => d.ToUniversalTime(),
        _ => value,
    };

    private async Task<int> ExecAsync(string sql, CancellationToken ct, params object?[] args)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = Cmd(conn, null, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params object?[] args)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = Cmd(conn, null, sql, args);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default : (T)result;
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = Cmd(conn, null, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }

    private static Guid? NullableGuid(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static string? NullableString(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset? NullableTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
    private static DateTimeOffset Time(NpgsqlDataReader r, int i) => r.GetFieldValue<DateTimeOffset>(i);

    private static UserRecord MapUser(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetBoolean(1), Time(r, 2));
    private static ProfileRecord MapProfile(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3).Trim(), r.GetInt32(4), Time(r, 5));
    private static AuthCodeRecord MapCode(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetFieldValue<byte[]>(2), NullableGuid(r, 3), NullableString(r, 4), Time(r, 5), r.GetInt32(6));
    private static RefreshTokenRecord MapRefresh(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<byte[]>(2), Time(r, 3), Time(r, 4), NullableTime(r, 5), NullableGuid(r, 6));

    private static string ModeText(MatchMode m) => m == MatchMode.Private ? "private" : "matchmaking";
    private static string GameModeText(LudoCrown.Core.Model.GameMode m) => m.ToString().ToLowerInvariant();
    private static LudoCrown.Core.Model.GameMode ParseGameMode(string s) => LudoCrown.Core.Model.GameModes.Parse(s) ?? LudoCrown.Core.Model.GameMode.Classic;
    private static MatchMode ParseMode(string s) => s == "private" ? MatchMode.Private : MatchMode.Matchmaking;
    private static string ResultText(ParticipantResult r) => r switch { ParticipantResult.Win => "win", ParticipantResult.Abandoned => "abandoned", _ => "loss" };
    private static ParticipantResult ParseResult(string s) => s switch { "win" => ParticipantResult.Win, "abandoned" => ParticipantResult.Abandoned, _ => ParticipantResult.Loss };
    private static (Guid Low, Guid High) Pair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);

    // ---------------- accounts ----------------

    public async Task<UserRecord> CreateUserAsync(bool isGuest, string displayName, string avatarId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var user = new UserRecord(Guid.NewGuid(), isGuest, now);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var c = Cmd(conn, tx, Sql.InsertUser, user.Id, isGuest, now)) await c.ExecuteNonQueryAsync(ct);
        // Friend codes are random; retry on the (rare) unique collision.
        for (var attempt = 0; ; attempt++)
        {
            await using var c = Cmd(conn, tx, Sql.InsertProfile, user.Id, displayName, avatarId, InMemoryStore.NewFriendCode(), now);
            if (await c.ExecuteNonQueryAsync(ct) == 1) break;
            if (attempt > 10) throw new InvalidOperationException("Could not allocate a friend code.");
        }
        await using (var c = Cmd(conn, tx, Sql.InsertStats, user.Id)) await c.ExecuteNonQueryAsync(ct);
        await using (var c = Cmd(conn, tx, Sql.InsertWallet, user.Id, (long)Economy.StarterGems)) await c.ExecuteNonQueryAsync(ct);
        await using (var c = Cmd(conn, tx, Sql.InsertGemLedger, user.Id, (long)Economy.StarterGems, Economy.Reasons.StarterGrant, null, "starter")) await c.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return user;
    }

    public async Task<UserRecord?> GetUserAsync(Guid userId, CancellationToken ct = default) =>
        (await QueryAsync(Sql.GetUser, MapUser, ct, userId)).FirstOrDefault();

    public async Task<Guid?> FindUserByIdentityAsync(string provider, string subject, CancellationToken ct = default)
    {
        var id = await ScalarAsync<Guid>(Sql.FindUserByIdentity, ct, provider, subject);
        return id == Guid.Empty ? null : id;
    }

    public async Task<bool> AddIdentityAsync(Guid userId, string provider, string subject, string? email, CancellationToken ct = default)
    {
        if (await ExecAsync(Sql.InsertIdentity, ct, userId, provider, subject, email, time.GetUtcNow()) == 1) return true;
        return await FindUserByIdentityAsync(provider, subject, ct) == userId;
    }

    public async Task<IReadOnlyList<IdentityRecord>> GetIdentitiesAsync(Guid userId, CancellationToken ct = default) =>
        await QueryAsync(Sql.GetIdentities, r => new IdentityRecord(r.GetGuid(0), r.GetString(1), r.GetString(2), NullableString(r, 3), Time(r, 4)), ct, userId);

    public Task SetGuestAsync(Guid userId, bool isGuest, CancellationToken ct = default) => ExecAsync(Sql.SetGuest, ct, userId, isGuest);

    public async Task DeleteUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Keep opponents' match history readable but strip identity: the seat becomes "Deleted player"
        // and user_id is set NULL by the foreign key when the user row is deleted.
        await using (var c = Cmd(conn, tx, Sql.AnonymiseParticipants, userId)) await c.ExecuteNonQueryAsync(ct);
        await using (var c = Cmd(conn, tx, Sql.DeleteReportsBy, userId)) await c.ExecuteNonQueryAsync(ct);
        await using (var c = Cmd(conn, tx, Sql.DeleteUser, userId)) await c.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public Task StoreRefreshTokenAsync(RefreshTokenRecord t, CancellationToken ct = default) =>
        ExecAsync(Sql.InsertRefreshToken, ct, t.Id, t.UserId, t.TokenHash, t.CreatedAt, t.ExpiresAt);

    public async Task<RefreshTokenRecord?> GetRefreshTokenAsync(byte[] tokenHash, CancellationToken ct = default) =>
        (await QueryAsync(Sql.GetRefreshToken, MapRefresh, ct, tokenHash)).FirstOrDefault();

    public Task RevokeRefreshTokenAsync(Guid tokenId, Guid? replacedBy, DateTimeOffset at, CancellationToken ct = default) =>
        ExecAsync(Sql.RevokeRefreshToken, ct, tokenId, replacedBy, at);

    public Task RevokeAllRefreshTokensAsync(Guid userId, DateTimeOffset at, CancellationToken ct = default) =>
        ExecAsync(Sql.RevokeAllRefreshTokens, ct, userId, at);

    public Task CreateAuthCodeAsync(AuthCodeRecord c, CancellationToken ct = default) =>
        ExecAsync(Sql.InsertAuthCode, ct, c.Id, c.Kind, c.CodeHash, c.UserId, c.Payload, c.ExpiresAt, c.Attempts);

    public async Task<AuthCodeRecord?> ConsumeAuthCodeAsync(string kind, byte[] codeHash, DateTimeOffset now, CancellationToken ct = default) =>
        (await QueryAsync(Sql.ConsumeAuthCode, MapCode, ct, kind, codeHash, now)).FirstOrDefault();

    public async Task<AuthCodeRecord?> FindLiveAuthCodeAsync(string kind, string payload, DateTimeOffset now, CancellationToken ct = default) =>
        (await QueryAsync(Sql.FindLiveAuthCode, MapCode, ct, kind, payload, now)).FirstOrDefault();

    public async Task<int> IncrementAuthCodeAttemptsAsync(Guid codeId, CancellationToken ct = default) =>
        (await QueryAsync(Sql.IncrementAuthCodeAttempts, r => r.GetInt32(0), ct, codeId)).DefaultIfEmpty(int.MaxValue).First();

    public Task InvalidateAuthCodesAsync(string kind, string payload, CancellationToken ct = default) =>
        ExecAsync(Sql.InvalidateAuthCodes, ct, kind, payload);

    // ---------------- profiles ----------------

    public async Task<ProfileRecord?> GetProfileAsync(Guid userId, CancellationToken ct = default) =>
        (await QueryAsync(Sql.GetProfile, MapProfile, ct, userId)).FirstOrDefault();

    public async Task<IReadOnlyDictionary<Guid, ProfileRecord>> GetProfilesAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, ProfileRecord>();
        return (await QueryAsync(Sql.GetProfiles, MapProfile, ct, ids)).ToDictionary(p => p.UserId);
    }

    public async Task<ProfileRecord?> GetProfileByFriendCodeAsync(string friendCode, CancellationToken ct = default) =>
        (await QueryAsync(Sql.GetProfileByFriendCode, MapProfile, ct, friendCode)).FirstOrDefault();

    public async Task<ProfileRecord> UpdateProfileAsync(Guid userId, string? displayName, string? avatarId, DateTimeOffset at, CancellationToken ct = default) =>
        (await QueryAsync(Sql.UpdateProfile, MapProfile, ct, userId, displayName, avatarId, at)).Single();

    public async Task<StatsRecord> GetStatsAsync(Guid userId, CancellationToken ct = default) =>
        (await QueryAsync(Sql.GetStats, r => new StatsRecord(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8)), ct, userId))
        .FirstOrDefault() ?? StatsRecord.Empty(userId);

    public async Task<long> GetCoinsAsync(Guid userId, CancellationToken ct = default) => await ScalarAsync<long>(Sql.GetCoins, ct, userId);

    // ---------------- matches ----------------

    public async Task CreateMatchAsync(MatchRecord match, string initialStateJson, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var c = Cmd(conn, tx, Sql.InsertMatch, match.Id, ModeText(match.Mode), match.PlayerCount, match.RoomCode, match.StartedAt, initialStateJson, GameModeText(match.GameMode)))
            await c.ExecuteNonQueryAsync(ct);
        foreach (var p in match.Participants)
        {
            await using var c = Cmd(conn, tx, Sql.InsertParticipant, match.Id, p.Seat, p.UserId, p.IsBot, p.DisplayName, p.Color);
            await c.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public Task SaveLiveStateAsync(Guid matchId, string stateJson, long version, CancellationToken ct = default) =>
        ExecAsync(Sql.SaveLiveState, ct, matchId, stateJson, version);

    public async Task<bool> CompleteMatchAsync(MatchCompletion c, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Row lock + status check makes completion idempotent even if called twice concurrently.
        await using (var lockCmd = Cmd(conn, tx, Sql.LockActiveMatch, c.MatchId))
            if (await lockCmd.ExecuteScalarAsync(ct) is null) { await tx.RollbackAsync(ct); return false; }

        var status = c.Status == MatchStatus.Abandoned ? "abandoned" : "completed";
        await using (var cmd = Cmd(conn, tx, Sql.FinishMatch, c.MatchId, status, c.EndedAt, c.WinnerUserId, c.FinalStateJson))
            await cmd.ExecuteNonQueryAsync(ct);

        foreach (var o in c.Outcomes)
        {
            await using (var cmd = Cmd(conn, tx, Sql.RecordParticipantResult, c.MatchId, o.Seat, o.Placement, ResultText(o.Result), o.CoinsAwarded, o.XpAwarded, o.PiecesCaptured,
                o.RerollsUsed, o.GemsAwarded))
                await cmd.ExecuteNonQueryAsync(ct);
            if (o.UserId is not Guid uid) continue;

            await using (var cmd = Cmd(conn, tx, Sql.ApplyStats, uid, o.Result == ParticipantResult.Win, c.PlayerCount, o.PiecesCaptured, o.Result == ParticipantResult.Abandoned))
                await cmd.ExecuteNonQueryAsync(ct);
            if (o.XpAwarded > 0)
                await using (var cmd = Cmd(conn, tx, Sql.AddXp, uid, o.XpAwarded)) await cmd.ExecuteNonQueryAsync(ct);
            if (o.CoinsAwarded > 0)
            {
                await using var ledger = Cmd(conn, tx, Sql.InsertLedger, uid, (long)o.CoinsAwarded, c.MatchId);
                if (await ledger.ExecuteNonQueryAsync(ct) == 1)
                    await using (var cmd = Cmd(conn, tx, Sql.AddCoins, uid, (long)o.CoinsAwarded)) await cmd.ExecuteNonQueryAsync(ct);
            }
            if (o.GemsAwarded > 0)
                await using (var gem = Cmd(conn, tx, Sql.CreditGemsOnce, uid, (long)o.GemsAwarded, Economy.Reasons.MatchReward, c.MatchId, null))
                    await gem.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ActiveMatchRecord>> GetActiveMatchesAsync(CancellationToken ct = default)
    {
        var matches = await QueryAsync(Sql.GetActiveMatches,
            r => (Id: r.GetGuid(0), Mode: ParseMode(r.GetString(1)), Count: (int)r.GetInt16(2), Code: NullableString(r, 3), Started: Time(r, 4), Json: r.GetString(5), Game: ParseGameMode(r.GetString(6))), ct);
        if (matches.Count == 0) return Array.Empty<ActiveMatchRecord>();
        var participants = await QueryAsync(Sql.GetParticipants,
            r => (MatchId: r.GetGuid(0), P: new MatchParticipantRecord(r.GetInt16(1), NullableGuid(r, 2), r.GetBoolean(3), r.GetString(4), r.GetString(5))),
            ct, matches.Select(m => m.Id).ToArray());
        return matches.Select(m => new ActiveMatchRecord(
            new MatchRecord(m.Id, m.Mode, m.Count, m.Code?.Trim(), m.Started, participants.Where(p => p.MatchId == m.Id).Select(p => p.P).ToList(), m.Game),
            m.Json)).ToList();
    }

    public async Task<IReadOnlyList<MatchHistoryItem>> GetHistoryAsync(Guid userId, int limit, CancellationToken ct = default) =>
        await QueryAsync(Sql.GetHistory, r => new MatchHistoryItem(
            r.GetGuid(0), ParseMode(r.GetString(1)), r.GetInt16(2), Time(r, 3), NullableTime(r, 4),
            r.IsDBNull(5) ? 0 : r.GetInt16(5), ParseResult(r.GetString(6)), r.GetInt32(7), r.GetInt32(8),
            r.GetFieldValue<string[]>(9), ParseGameMode(r.GetString(10)), r.GetInt16(11), r.GetInt32(12)), ct, userId, limit);

    // ---------------- gems ----------------

    public async Task<long> GetGemsAsync(Guid userId, CancellationToken ct = default) => await ScalarAsync<long>(Sql.GetGems, ct, userId);

    /// <summary>
    /// Idempotent, atomic gem spend. The debit and its ledger row are one SQL statement (see Sql.SpendGemsOnce), so the
    /// balance can never go negative and a key is charged at most once, even for concurrent retries on several servers.
    /// </summary>
    public async Task<GemSpendResult> SpendGemsAsync(Guid userId, Guid? matchId, string idempotencyKey, int cost, string reason, DateTimeOffset at, CancellationToken ct = default)
    {
        if (cost <= 0) throw new ArgumentOutOfRangeException(nameof(cost));
        await using var conn = await db.OpenConnectionAsync(ct);
        object? balance;
        try
        {
            await using var spend = Cmd(conn, null, Sql.SpendGemsOnce, userId, (long)cost, reason, matchId, idempotencyKey);
            balance = await spend.ExecuteScalarAsync(ct);
        }
        catch (System.Data.Common.DbException)
        {
            // Lost a race against the same key (unique violation): the statement charged nothing.
            var (used, current) = await SpendStateAsync(conn, userId, idempotencyKey, ct);
            if (!used) throw;
            return new GemSpendResult(GemSpendOutcome.AlreadySpent, current ?? 0);
        }
        if (balance is long newBalance) return new GemSpendResult(GemSpendOutcome.Spent, newBalance);

        var (keyUsed, gems) = await SpendStateAsync(conn, userId, idempotencyKey, ct);
        if (keyUsed) return new GemSpendResult(GemSpendOutcome.AlreadySpent, gems ?? 0);
        return gems is long bal
            ? new GemSpendResult(GemSpendOutcome.InsufficientGems, bal)
            : new GemSpendResult(GemSpendOutcome.UnknownUser, 0);
    }

    private static async Task<(bool KeyUsed, long? Gems)> SpendStateAsync(NpgsqlConnection conn, Guid userId, string key, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, Sql.GemSpendState, userId, key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetInt64(1));
    }

    public async Task<long> GrantGemsAsync(Guid userId, int amount, string reason, string idempotencyKey, DateTimeOffset at, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var credit = Cmd(conn, null, Sql.CreditGemsOnce, userId, (long)amount, reason, null, idempotencyKey))
            await credit.ExecuteNonQueryAsync(ct);
        return await GetGemsAsync(userId, ct);
    }

    // ---------------- social ----------------

    public async Task<IReadOnlyList<FriendRecord>> GetFriendsAsync(Guid userId, CancellationToken ct = default) =>
        await QueryAsync(Sql.GetFriends, r =>
        {
            var status = r.GetString(4) == "accepted" ? FriendshipStatus.Accepted : FriendshipStatus.Pending;
            return new FriendRecord(r.GetGuid(0), r.GetString(1), r.GetString(2), Progression.LevelForXp(r.GetInt32(3)), status,
                status == FriendshipStatus.Pending && r.GetGuid(5) != userId);
        }, ct, userId);

    public async Task<FriendshipStatus> RequestFriendAsync(Guid fromUser, Guid toUser, DateTimeOffset at, CancellationToken ct = default)
    {
        var (low, high) = Pair(fromUser, toUser);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var ins = Cmd(conn, tx, Sql.InsertFriendship, low, high, fromUser, at)) await ins.ExecuteNonQueryAsync(ct);

        string status;
        Guid requestedBy;
        await using (var get = Cmd(conn, tx, Sql.GetFriendship, low, high))
        await using (var r = await get.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            status = r.GetString(0);
            requestedBy = r.GetGuid(1);
        }
        // The other player had already asked: asking back accepts.
        if (status == "pending" && requestedBy != fromUser)
        {
            await using var acc = Cmd(conn, tx, Sql.AcceptFriendship, low, high, requestedBy);
            await acc.ExecuteNonQueryAsync(ct);
            status = "accepted";
        }
        await tx.CommitAsync(ct);
        return status == "accepted" ? FriendshipStatus.Accepted : FriendshipStatus.Pending;
    }

    public async Task<bool> AcceptFriendAsync(Guid userId, Guid requesterId, CancellationToken ct = default)
    {
        var (low, high) = Pair(userId, requesterId);
        return await ExecAsync(Sql.AcceptFriendship, ct, low, high, requesterId) == 1;
    }

    public Task RemoveFriendAsync(Guid userId, Guid otherId, CancellationToken ct = default)
    {
        var (low, high) = Pair(userId, otherId);
        return ExecAsync(Sql.DeleteFriendship, ct, low, high);
    }

    public async Task BlockAsync(Guid blockerId, Guid blockedId, DateTimeOffset at, CancellationToken ct = default)
    {
        var (low, high) = Pair(blockerId, blockedId);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var c = Cmd(conn, tx, Sql.InsertBlock, blockerId, blockedId, at)) await c.ExecuteNonQueryAsync(ct);
        await using (var c = Cmd(conn, tx, Sql.DeleteFriendship, low, high)) await c.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public Task UnblockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default) => ExecAsync(Sql.DeleteBlock, ct, blockerId, blockedId);

    public async Task<IReadOnlyList<Guid>> GetBlockedAsync(Guid userId, CancellationToken ct = default) =>
        await QueryAsync(Sql.GetBlocked, r => r.GetGuid(0), ct, userId);

    public async Task<bool> IsBlockedEitherWayAsync(Guid a, Guid b, CancellationToken ct = default) =>
        await ScalarAsync<bool>(Sql.IsBlockedEitherWay, ct, a, b);

    public Task AddReportAsync(ReportRecord r, CancellationToken ct = default) =>
        ExecAsync(Sql.InsertReport, ct, r.Id, r.ReporterId, r.ReportedId, r.MatchId, r.Reason, r.Details, r.CreatedAt);
}
