using LudoCrown.Core.Model;

namespace LudoCrown.Server.Data;

/// <summary>In-memory economy, purchases, profile settings and league (same rules as the PostgreSQL store).</summary>
public sealed partial class InMemoryStore
{
    private readonly HashSet<(Guid User, string Key)> _operations = new();
    private readonly Dictionary<(string Store, string Tx), PurchaseRecord> _purchases = new();

    private sealed record GroupRow(long Id, int SeasonId, int Division, int Members);
    private readonly Dictionary<int, SeasonRecord> _seasons = new();
    private readonly Dictionary<long, GroupRow> _groups = new();
    private readonly Dictionary<Guid, int> _leaguePlayers = new();
    private readonly Dictionary<(int Season, Guid User), StandingRecord> _standings = new();
    private readonly Dictionary<(int Season, Guid User), RewardRecord> _rewards = new();
    private int _nextSeasonId = 1;
    private long _nextGroupId = 1;

    /// <summary>Every ledger entry (tests inspect it).</summary>
    public IReadOnlyList<(Guid User, LedgerEntry Entry)> Ledger { get { lock (_gate) return _ledger.ToList(); } }

    private void AddLedger(Guid user, string asset, long delta, string reason, DateTimeOffset at, Guid? matchId, Guid? purchaseId) =>
        _ledger.Add((user, new LedgerEntry(asset, delta, reason, at, matchId, purchaseId)));

    // ---------- profile extras ----------

    public Task<IReadOnlyList<ModeStatsRecord>> GetModeStatsAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ModeStatsRecord>>(GameModes.All
                .Select(m => _modeStats.GetValueOrDefault((userId, m.Mode)) ?? ModeStatsRecord.Empty(m.Mode)).ToList());
    }

    public Task<PrivacySettings> GetPrivacyAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_privacy.GetValueOrDefault(userId) ?? PrivacySettings.Default);
    }

    public Task<PrivacySettings> UpdatePrivacyAsync(Guid userId, bool? showStats, bool? showLeague, bool? chatEnabled, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var p = _privacy.GetValueOrDefault(userId) ?? PrivacySettings.Default;
            p = new PrivacySettings(showStats ?? p.ShowStats, showLeague ?? p.ShowLeague, chatEnabled ?? p.ChatEnabled);
            if (_users.ContainsKey(userId)) _privacy[userId] = p;
            return Task.FromResult(p);
        }
    }

    public Task SetFrameAsync(Guid userId, string? frameId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_profiles.TryGetValue(userId, out var p)) _profiles[userId] = p with { FrameId = frameId };
            return Task.CompletedTask;
        }
    }

    // ---------- economy ----------

    private Balances BalancesOf(Guid userId) => new(_coins.GetValueOrDefault(userId), _gems.GetValueOrDefault(userId),
        _items.Where(i => i.Key.User == userId && i.Value > 0).ToDictionary(i => i.Key.Item, i => i.Value));

    public Task<Balances?> GetBalancesAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_users.ContainsKey(userId) ? BalancesOf(userId) : null);
    }

    public Task<EconomyResult> ApplyAsync(EconomyOperation op, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_users.ContainsKey(op.UserId)) return Task.FromResult(new EconomyResult(EconomyOutcome.UnknownUser, Balances.Empty));
            if (_operations.Contains((op.UserId, op.IdempotencyKey)))
                return Task.FromResult(new EconomyResult(EconomyOutcome.AlreadyApplied, BalancesOf(op.UserId)));

            var changes = op.Normalized();
            // Check every change first: all or nothing.
            foreach (var c in changes)
            {
                var after = c.Asset switch
                {
                    Economy.Coins => _coins.GetValueOrDefault(op.UserId) + c.Delta,
                    Economy.Gems => _gems.GetValueOrDefault(op.UserId) + c.Delta,
                    _ => _items.GetValueOrDefault((op.UserId, c.Asset)) + c.Delta,
                };
                var max = c.Asset is Economy.Coins or Economy.Gems ? long.MaxValue : (Items.Get(c.Asset)?.MaxQuantity ?? 0);
                if (after < 0 || after > max) return Task.FromResult(new EconomyResult(EconomyOutcome.Insufficient, BalancesOf(op.UserId)));
            }
            foreach (var c in changes)
            {
                switch (c.Asset)
                {
                    case Economy.Coins: _coins[op.UserId] = _coins.GetValueOrDefault(op.UserId) + c.Delta; break;
                    case Economy.Gems: _gems[op.UserId] = _gems.GetValueOrDefault(op.UserId) + c.Delta; break;
                    default: _items[(op.UserId, c.Asset)] = _items.GetValueOrDefault((op.UserId, c.Asset)) + (int)c.Delta; break;
                }
                AddLedger(op.UserId, c.Asset, c.Delta, op.Reason, op.At, op.MatchId, op.PurchaseId);
            }
            _operations.Add((op.UserId, op.IdempotencyKey));
            return Task.FromResult(new EconomyResult(EconomyOutcome.Applied, BalancesOf(op.UserId)));
        }
    }

    public Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<LedgerEntry>>(_ledger.Where(l => l.User == userId).Select(l => l.Entry).Reverse().Take(limit).ToList());
    }

    // ---------- purchases ----------

    public Task<PurchaseRecord?> GetPurchaseAsync(string store, string transactionId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_purchases.GetValueOrDefault((store, transactionId)));
    }

    public Task<(PurchaseRecord Record, bool Created)> CreatePurchaseAsync(PurchaseRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_purchases.TryGetValue((record.Store, record.TransactionId), out var existing)) return Task.FromResult((existing, false));
            _purchases[(record.Store, record.TransactionId)] = record;
            return Task.FromResult((record, true));
        }
    }

    public Task UpdatePurchaseAsync(Guid purchaseId, PurchaseStatus status, string? failureReason, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var hit = _purchases.Values.FirstOrDefault(p => p.Id == purchaseId);
            if (hit is not null)
                _purchases[(hit.Store, hit.TransactionId)] = hit with
                {
                    Status = status,
                    FailureReason = failureReason,
                    CreditedAt = status == PurchaseStatus.Credited ? at : hit.CreditedAt,
                    RefundedAt = status == PurchaseStatus.Refunded ? at : hit.RefundedAt,
                };
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<PurchaseRecord>> GetPurchasesForUserAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PurchaseRecord>>(_purchases.Values.Where(p => p.UserId == userId).OrderByDescending(p => p.CreatedAt).Take(limit).ToList());
    }

    // ---------- league ----------

    public Task<SeasonRecord?> GetCurrentSeasonAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_seasons.Values.Where(s => s.Status != SeasonStatus.Completed).OrderBy(s => s.Number).FirstOrDefault());
    }

    public Task<SeasonRecord?> GetLatestSeasonAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_seasons.Values.OrderByDescending(s => s.Number).FirstOrDefault());
    }

    public Task<SeasonRecord?> GetSeasonByNumberAsync(int number, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_seasons.Values.FirstOrDefault(s => s.Number == number));
    }

    public Task<SeasonRecord> CreateSeasonAsync(int number, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _seasons.Values.FirstOrDefault(s => s.Number == number);
            if (existing is not null) return Task.FromResult(existing);
            var season = new SeasonRecord(_nextSeasonId++, number, startsAt, endsAt, SeasonStatus.Active, null);
            _seasons[season.Id] = season;
            return Task.FromResult(season);
        }
    }

    public Task SetSeasonStatusAsync(int seasonId, SeasonStatus status, DateTimeOffset? finalizedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_seasons.TryGetValue(seasonId, out var s)) _seasons[seasonId] = s with { Status = status, FinalizedAt = finalizedAt ?? s.FinalizedAt };
            return Task.CompletedTask;
        }
    }

    public Task<int?> GetDivisionAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<int?>(_leaguePlayers.TryGetValue(userId, out var d) ? d : null);
    }

    public Task<StandingRecord> EnsureStandingAsync(int seasonId, Guid userId, int groupSize, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_standings.TryGetValue((seasonId, userId), out var existing)) return Task.FromResult(existing);
            if (!_users.ContainsKey(userId)) throw new InvalidOperationException("Unknown user.");
            if (!_leaguePlayers.TryGetValue(userId, out var division)) _leaguePlayers[userId] = division = 0;
            var group = _groups.Values.Where(g => g.SeasonId == seasonId && g.Division == division && g.Members < groupSize).OrderBy(g => g.Id).FirstOrDefault()
                        ?? new GroupRow(_nextGroupId++, seasonId, division, 0);
            _groups[group.Id] = group with { Members = group.Members + 1 };
            var standing = new StandingRecord(seasonId, userId, group.Id, division, 0, 0, 0, null);
            _standings[(seasonId, userId)] = standing;
            return Task.FromResult(standing);
        }
    }

    public Task<StandingRecord?> GetStandingAsync(int seasonId, Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_standings.GetValueOrDefault((seasonId, userId)));
    }

    public Task<IReadOnlyList<StandingEntry>> GetGroupAsync(long groupId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StandingEntry> rows = _standings.Values.Where(s => s.GroupId == groupId && _profiles.ContainsKey(s.UserId))
                .OrderByDescending(s => s.Points).ThenByDescending(s => s.Wins).ThenBy(s => s.LastPointsAt ?? DateTimeOffset.MaxValue).ThenBy(s => s.UserId)
                .Take(limit)
                .Select(s =>
                {
                    var p = _profiles[s.UserId];
                    return new StandingEntry(s.UserId, p.FriendCode, p.DisplayName, p.AvatarId, p.FrameId, s.Points, s.MatchesPlayed, s.Wins, s.LastPointsAt);
                }).ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<IReadOnlyList<StandingRecord>> GetSeasonStandingsAsync(int seasonId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<StandingRecord>>(_standings.Values.Where(s => s.SeasonId == seasonId).ToList());
    }

    public Task SaveSeasonResultsAsync(int seasonId, IReadOnlyList<SeasonResult> results, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var r in results)
            {
                if (!_standings.TryGetValue((seasonId, r.UserId), out var st)) continue;
                _standings[(seasonId, r.UserId)] = st with { FinalRank = r.FinalRank, Outcome = r.Outcome };
                _leaguePlayers[r.UserId] = r.NextDivision;
                if (r.Reward is { IsEmpty: false } reward && !_rewards.ContainsKey((seasonId, r.UserId)))
                    _rewards[(seasonId, r.UserId)] = new RewardRecord(seasonId, r.UserId, st.Division, r.FinalRank, reward, null);
            }
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<RewardRecord>> GetUngrantedRewardsAsync(int seasonId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<RewardRecord>>(_rewards.Values.Where(r => r.SeasonId == seasonId && r.GrantedAt is null).ToList());
    }

    public Task MarkRewardGrantedAsync(int seasonId, Guid userId, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_rewards.TryGetValue((seasonId, userId), out var r) && r.GrantedAt is null) _rewards[(seasonId, userId)] = r with { GrantedAt = at };
            return Task.CompletedTask;
        }
    }

    public Task<RewardRecord?> GetRewardAsync(int seasonId, Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_rewards.GetValueOrDefault((seasonId, userId)));
    }
}
