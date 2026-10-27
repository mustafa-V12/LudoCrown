using System.Security.Cryptography;
using LudoCrown.Core.Protocol;

namespace LudoCrown.Server.Data;

/// <summary>
/// DEVELOPMENT/TEST store. Keeps everything in process memory; data is lost on restart.
/// Production uses the PostgreSQL store (LudoCrown.Server.Postgres). The server refuses to start
/// with this store outside Development unless explicitly allowed (see Program.cs).
/// </summary>
public sealed class InMemoryStore : IAccountStore, IProfileStore, IMatchStore, ISocialStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;

    private readonly Dictionary<Guid, UserRecord> _users = new();
    private readonly List<IdentityRecord> _identities = new();
    private readonly Dictionary<Guid, ProfileRecord> _profiles = new();
    private readonly Dictionary<Guid, StatsRecord> _stats = new();
    private readonly Dictionary<Guid, long> _coins = new();
    private readonly Dictionary<Guid, RefreshTokenRecord> _refresh = new();
    private readonly Dictionary<Guid, (AuthCodeRecord Code, bool Consumed)> _codes = new();

    private sealed class StoredMatch
    {
        public required MatchRecord Match;
        public string StateJson = "";
        public long Version;
        public MatchStatus Status = MatchStatus.Active;
        public DateTimeOffset? EndedAt;
        public List<ParticipantOutcome> Outcomes = new();
        public List<MatchParticipantRecord> Participants = new();
    }

    private readonly Dictionary<Guid, StoredMatch> _matches = new();
    private readonly Dictionary<(Guid Low, Guid High), (FriendshipStatus Status, Guid RequestedBy)> _friends = new();
    private readonly HashSet<(Guid Blocker, Guid Blocked)> _blocks = new();
    private readonly List<ReportRecord> _reports = new();

    public InMemoryStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public IReadOnlyList<ReportRecord> Reports { get { lock (_gate) return _reports.ToList(); } }

    // ---------- accounts ----------

    public Task<UserRecord> CreateUserAsync(bool isGuest, string displayName, string avatarId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var user = new UserRecord(Guid.NewGuid(), isGuest, now);
            _users[user.Id] = user;
            string code;
            do code = NewFriendCode(); while (_profiles.Values.Any(p => p.FriendCode == code));
            _profiles[user.Id] = new ProfileRecord(user.Id, displayName, avatarId, code, 0, now);
            _stats[user.Id] = StatsRecord.Empty(user.Id);
            _coins[user.Id] = 0;
            return Task.FromResult(user);
        }
    }

    public Task<UserRecord?> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_users.GetValueOrDefault(userId));
    }

    public Task<Guid?> FindUserByIdentityAsync(string provider, string subject, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_identities.FirstOrDefault(i => i.Provider == provider && i.Subject == subject)?.UserId);
    }

    public Task<bool> AddIdentityAsync(Guid userId, string provider, string subject, string? email, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _identities.FirstOrDefault(i => i.Provider == provider && i.Subject == subject);
            if (existing is not null) return Task.FromResult(existing.UserId == userId);
            _identities.Add(new IdentityRecord(userId, provider, subject, email, _time.GetUtcNow()));
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<IdentityRecord>> GetIdentitiesAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<IdentityRecord>>(_identities.Where(i => i.UserId == userId).ToList());
    }

    public Task SetGuestAsync(Guid userId, bool isGuest, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_users.TryGetValue(userId, out var u)) _users[userId] = u with { IsGuest = isGuest };
            return Task.CompletedTask;
        }
    }

    public Task DeleteUserAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _users.Remove(userId);
            _identities.RemoveAll(i => i.UserId == userId);
            _profiles.Remove(userId);
            _stats.Remove(userId);
            _coins.Remove(userId);
            foreach (var key in _refresh.Where(r => r.Value.UserId == userId).Select(r => r.Key).ToList()) _refresh.Remove(key);
            foreach (var key in _friends.Keys.Where(k => k.Low == userId || k.High == userId).ToList()) _friends.Remove(key);
            _blocks.RemoveWhere(b => b.Blocker == userId || b.Blocked == userId);
            foreach (var m in _matches.Values)
            {
                m.Participants = m.Participants.Select(p => p.UserId == userId ? p with { UserId = null, DisplayName = "Deleted player" } : p).ToList();
                m.Outcomes = m.Outcomes.Select(o => o.UserId == userId ? o with { UserId = null } : o).ToList();
            }
            _reports.RemoveAll(r => r.ReporterId == userId);
            return Task.CompletedTask;
        }
    }

    public Task StoreRefreshTokenAsync(RefreshTokenRecord token, CancellationToken ct = default)
    {
        lock (_gate) { _refresh[token.Id] = token; return Task.CompletedTask; }
    }

    public Task<RefreshTokenRecord?> GetRefreshTokenAsync(byte[] tokenHash, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_refresh.Values.FirstOrDefault(r => r.TokenHash.AsSpan().SequenceEqual(tokenHash)));
    }

    public Task RevokeRefreshTokenAsync(Guid tokenId, Guid? replacedBy, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_refresh.TryGetValue(tokenId, out var t) && t.RevokedAt is null) _refresh[tokenId] = t with { RevokedAt = at, ReplacedBy = replacedBy };
            return Task.CompletedTask;
        }
    }

    public Task RevokeAllRefreshTokensAsync(Guid userId, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var t in _refresh.Values.Where(r => r.UserId == userId && r.RevokedAt is null).ToList())
                _refresh[t.Id] = t with { RevokedAt = at };
            return Task.CompletedTask;
        }
    }

    public Task CreateAuthCodeAsync(AuthCodeRecord code, CancellationToken ct = default)
    {
        lock (_gate) { _codes[code.Id] = (code, false); return Task.CompletedTask; }
    }

    public Task<AuthCodeRecord?> ConsumeAuthCodeAsync(string kind, byte[] codeHash, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var hit = _codes.Values.FirstOrDefault(c => !c.Consumed && c.Code.Kind == kind && c.Code.ExpiresAt > now && c.Code.CodeHash.AsSpan().SequenceEqual(codeHash));
            if (hit.Code is null) return Task.FromResult<AuthCodeRecord?>(null);
            _codes[hit.Code.Id] = (hit.Code, true);
            return Task.FromResult<AuthCodeRecord?>(hit.Code);
        }
    }

    public Task<AuthCodeRecord?> FindLiveAuthCodeAsync(string kind, string payload, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var hit = _codes.Values.Where(c => !c.Consumed && c.Code.Kind == kind && c.Code.Payload == payload && c.Code.ExpiresAt > now)
                .OrderByDescending(c => c.Code.ExpiresAt).Select(c => c.Code).FirstOrDefault();
            return Task.FromResult(hit);
        }
    }

    public Task<int> IncrementAuthCodeAttemptsAsync(Guid codeId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_codes.TryGetValue(codeId, out var c)) return Task.FromResult(int.MaxValue);
            var updated = c.Code with { Attempts = c.Code.Attempts + 1 };
            _codes[codeId] = (updated, c.Consumed);
            return Task.FromResult(updated.Attempts);
        }
    }

    public Task InvalidateAuthCodesAsync(string kind, string payload, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var c in _codes.Values.Where(c => c.Code.Kind == kind && c.Code.Payload == payload).ToList())
                _codes[c.Code.Id] = (c.Code, true);
            return Task.CompletedTask;
        }
    }

    // ---------- profiles ----------

    public Task<ProfileRecord?> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_profiles.GetValueOrDefault(userId));
    }

    public Task<IReadOnlyDictionary<Guid, ProfileRecord>> GetProfilesAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<Guid, ProfileRecord> result = userIds.Distinct().Where(_profiles.ContainsKey).ToDictionary(id => id, id => _profiles[id]);
            return Task.FromResult(result);
        }
    }

    public Task<ProfileRecord?> GetProfileByFriendCodeAsync(string friendCode, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_profiles.Values.FirstOrDefault(p => p.FriendCode == friendCode));
    }

    public Task<ProfileRecord> UpdateProfileAsync(Guid userId, string? displayName, string? avatarId, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var p = _profiles[userId];
            p = p with { DisplayName = displayName ?? p.DisplayName, AvatarId = avatarId ?? p.AvatarId, UpdatedAt = at };
            _profiles[userId] = p;
            return Task.FromResult(p);
        }
    }

    public Task<StatsRecord> GetStatsAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_stats.GetValueOrDefault(userId) ?? StatsRecord.Empty(userId));
    }

    public Task<long> GetCoinsAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_coins.GetValueOrDefault(userId));
    }

    // ---------- matches ----------

    public Task CreateMatchAsync(MatchRecord match, string initialStateJson, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _matches[match.Id] = new StoredMatch { Match = match, StateJson = initialStateJson, Participants = match.Participants.ToList() };
            return Task.CompletedTask;
        }
    }

    public Task SaveLiveStateAsync(Guid matchId, string stateJson, long version, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_matches.TryGetValue(matchId, out var m) && m.Status == MatchStatus.Active && version >= m.Version)
            {
                m.StateJson = stateJson;
                m.Version = version;
            }
            return Task.CompletedTask;
        }
    }

    public Task<bool> CompleteMatchAsync(MatchCompletion c, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_matches.TryGetValue(c.MatchId, out var m) || m.Status != MatchStatus.Active) return Task.FromResult(false);
            m.Status = c.Status;
            m.EndedAt = c.EndedAt;
            m.StateJson = c.FinalStateJson;
            m.Outcomes = c.Outcomes.ToList();

            foreach (var o in c.Outcomes.Where(o => o.UserId is not null))
            {
                var id = o.UserId!.Value;
                if (!_stats.TryGetValue(id, out var s)) continue;
                var won = o.Result == ParticipantResult.Win;
                var streak = won ? s.CurrentWinStreak + 1 : 0;
                _stats[id] = s with
                {
                    GamesPlayed = s.GamesPlayed + 1,
                    GamesWon = s.GamesWon + (won ? 1 : 0),
                    TwoPlayerWins = s.TwoPlayerWins + (won && c.PlayerCount == 2 ? 1 : 0),
                    FourPlayerWins = s.FourPlayerWins + (won && c.PlayerCount == 4 ? 1 : 0),
                    PiecesCaptured = s.PiecesCaptured + o.PiecesCaptured,
                    Abandons = s.Abandons + (o.Result == ParticipantResult.Abandoned ? 1 : 0),
                    CurrentWinStreak = streak,
                    BestWinStreak = Math.Max(s.BestWinStreak, streak),
                };
                _coins[id] = _coins.GetValueOrDefault(id) + o.CoinsAwarded;
                if (_profiles.TryGetValue(id, out var p)) _profiles[id] = p with { Xp = p.Xp + o.XpAwarded };
            }
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ActiveMatchRecord>> GetActiveMatchesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ActiveMatchRecord> list = _matches.Values.Where(m => m.Status == MatchStatus.Active)
                .Select(m => new ActiveMatchRecord(m.Match with { Participants = m.Participants }, m.StateJson)).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<MatchHistoryItem>> GetHistoryAsync(Guid userId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<MatchHistoryItem> list = _matches.Values
                .Where(m => m.Status != MatchStatus.Active && m.Outcomes.Any(o => o.UserId == userId))
                .OrderByDescending(m => m.EndedAt)
                .Take(limit)
                .Select(m =>
                {
                    var me = m.Outcomes.First(o => o.UserId == userId);
                    var opponents = m.Participants.Where(p => p.Seat != me.Seat).Select(p => p.DisplayName).ToList();
                    return new MatchHistoryItem(m.Match.Id, m.Match.Mode, m.Match.PlayerCount, m.Match.StartedAt, m.EndedAt, me.Placement, me.Result, me.CoinsAwarded, me.XpAwarded, opponents);
                })
                .ToList();
            return Task.FromResult(list);
        }
    }

    // ---------- social ----------

    private static (Guid, Guid) Pair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);

    public Task<IReadOnlyList<FriendRecord>> GetFriendsAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FriendRecord> list = _friends
                .Where(f => f.Key.Low == userId || f.Key.High == userId)
                .Select(f =>
                {
                    var other = f.Key.Low == userId ? f.Key.High : f.Key.Low;
                    var p = _profiles.GetValueOrDefault(other);
                    return p is null ? null : new FriendRecord(other, p.DisplayName, p.AvatarId, p.Level, f.Value.Status, f.Value.Status == FriendshipStatus.Pending && f.Value.RequestedBy != userId);
                })
                .Where(f => f is not null)
                .Select(f => f!)
                .OrderBy(f => f.DisplayName)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<FriendshipStatus> RequestFriendAsync(Guid fromUser, Guid toUser, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Pair(fromUser, toUser);
            if (_friends.TryGetValue(key, out var existing))
            {
                if (existing.Status == FriendshipStatus.Pending && existing.RequestedBy != fromUser)
                {
                    _friends[key] = (FriendshipStatus.Accepted, existing.RequestedBy);
                    return Task.FromResult(FriendshipStatus.Accepted);
                }
                return Task.FromResult(existing.Status);
            }
            _friends[key] = (FriendshipStatus.Pending, fromUser);
            return Task.FromResult(FriendshipStatus.Pending);
        }
    }

    public Task<bool> AcceptFriendAsync(Guid userId, Guid requesterId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Pair(userId, requesterId);
            if (_friends.TryGetValue(key, out var f) && f.Status == FriendshipStatus.Pending && f.RequestedBy == requesterId)
            {
                _friends[key] = (FriendshipStatus.Accepted, requesterId);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    public Task RemoveFriendAsync(Guid userId, Guid otherId, CancellationToken ct = default)
    {
        lock (_gate) { _friends.Remove(Pair(userId, otherId)); return Task.CompletedTask; }
    }

    public Task BlockAsync(Guid blockerId, Guid blockedId, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _blocks.Add((blockerId, blockedId));
            _friends.Remove(Pair(blockerId, blockedId));
            return Task.CompletedTask;
        }
    }

    public Task UnblockAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default)
    {
        lock (_gate) { _blocks.Remove((blockerId, blockedId)); return Task.CompletedTask; }
    }

    public Task<IReadOnlyList<Guid>> GetBlockedAsync(Guid userId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Guid>>(_blocks.Where(b => b.Blocker == userId).Select(b => b.Blocked).ToList());
    }

    public Task<bool> IsBlockedEitherWayAsync(Guid a, Guid b, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_blocks.Contains((a, b)) || _blocks.Contains((b, a)));
    }

    public Task AddReportAsync(ReportRecord report, CancellationToken ct = default)
    {
        lock (_gate) { _reports.Add(report); return Task.CompletedTask; }
    }

    public static string NewFriendCode()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = RoomCodes.Alphabet[RandomNumberGenerator.GetInt32(RoomCodes.Alphabet.Length)];
        return new string(chars);
    }
}
