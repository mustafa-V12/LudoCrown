using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Data;
using LudoCrown.Server.Options;
using Microsoft.Extensions.Options;

namespace LudoCrown.Server.Matches;

/// <summary>
/// Private tables for friends. The host creates a room and shares its 6-character code (or a
/// ludocrown://join/CODE link). When at least two people are seated the host can start; empty seats in a
/// four-player room are filled with bots. After a match, any player can ask for a rematch with the same people.
/// </summary>
public sealed class PrivateRoomService(MatchService matches, IProfileStore profiles, ISocialStore social, IMatchNotifier notifier, IOptions<MatchOptions> options, TimeProvider time)
{
    private sealed class Room
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required string Code { get; init; }
        public required int PlayerCount { get; init; }
        public required GameMode Mode { get; init; }
        public Guid Host { get; set; }
        public List<Guid> Members { get; } = new();
        public DateTimeOffset LastActivity { get; set; }
        public Guid? MatchId { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Room> _rooms = new();
    /// <summary>Finished private matches → who played, for rematches.</summary>
    private readonly Dictionary<Guid, (int PlayerCount, List<Guid> Members, GameMode Mode)> _played = new();

    public async Task<(RoomInfo? Room, ServerError? Error)> CreateAsync(Guid host, int playerCount, GameMode mode = GameMode.Classic)
    {
        if (CommandValidator.ValidatePlayerCount(playerCount) != ErrorCode.None) return (null, new ServerError(ErrorCode.InvalidRequest, "Choose 2 or 4 players."));
        if (!GameModes.IsAvailable(mode)) return (null, new ServerError(ErrorCode.InvalidMode, "That game mode isn't available."));
        if (matches.ActiveMatchFor(host) is not null) return (null, new ServerError(ErrorCode.InvalidRequest, "Finish your current match first."));
        Room room;
        lock (_gate)
        {
            RemoveFromRoomsLocked(host);
            string code;
            do code = RoomCodes.Generate(); while (_rooms.Values.Any(r => r.Code == code));
            room = new Room { Code = code, PlayerCount = playerCount, Mode = mode, Host = host, LastActivity = time.GetUtcNow() };
            room.Members.Add(host);
            _rooms[room.Id] = room;
        }
        return (await InfoAsync(room, host), null);
    }

    public async Task<(RoomInfo? Room, ServerError? Error)> JoinAsync(Guid user, string? rawCode)
    {
        var code = RoomCodes.Normalize(rawCode);
        if (code is null) return (null, new ServerError(ErrorCode.InvalidRoomCode, "That code doesn't look right. Codes have 6 letters and numbers."));
        if (matches.ActiveMatchFor(user) is not null) return (null, new ServerError(ErrorCode.InvalidRequest, "Finish your current match first."));

        Room? room;
        lock (_gate) room = _rooms.Values.FirstOrDefault(r => r.Code == code && r.MatchId is null);
        if (room is null) return (null, new ServerError(ErrorCode.RoomNotFound, "No open table with that code. It may have started or expired."));

        List<Guid> members;
        lock (_gate) members = room.Members.ToList();
        foreach (var m in members)
            if (m != user && await social.IsBlockedEitherWayAsync(m, user))
                return (null, new ServerError(ErrorCode.RoomNotFound, "No open table with that code. It may have started or expired."));

        lock (_gate)
        {
            if (!room.Members.Contains(user))
            {
                if (room.Members.Count >= room.PlayerCount) return (null, new ServerError(ErrorCode.RoomFull, "That table is full."));
                RemoveFromRoomsLocked(user);
                room.Members.Add(user);
            }
            room.LastActivity = time.GetUtcNow();
        }
        await BroadcastAsync(room);
        return (await InfoAsync(room, user), null);
    }

    public async Task<ServerError?> LeaveAsync(Guid user, string? roomId)
    {
        Room? room;
        lock (_gate)
        {
            room = Guid.TryParse(roomId, out var id) ? _rooms.GetValueOrDefault(id) : null;
            if (room is null || !room.Members.Remove(user)) return null;
            if (room.Members.Count == 0) { _rooms.Remove(room.Id); return null; }
            if (room.Host == user) room.Host = room.Members[0];
        }
        await BroadcastAsync(room);
        return null;
    }

    public async Task<(string? MatchId, ServerError? Error)> StartAsync(Guid user, string? roomId)
    {
        Room? room;
        List<Guid> members;
        lock (_gate)
        {
            room = Guid.TryParse(roomId, out var id) ? _rooms.GetValueOrDefault(id) : null;
            if (room is null || !room.Members.Contains(user)) return (null, new ServerError(ErrorCode.RoomNotFound, "Table not found."));
            if (room.Host != user) return (null, new ServerError(ErrorCode.InvalidRequest, "Only the host can start the game."));
            if (room.MatchId is not null) return (room.MatchId.Value.ToString("N"), null);
            if (room.Members.Count < 2) return (null, new ServerError(ErrorCode.InvalidRequest, "Wait for at least one friend to join."));
            members = room.Members.ToList();
            room.MatchId = Guid.Empty; // reserve: prevents a double start while we await
        }

        var seats = members.Select(m => (Guid?)m).ToList();
        while (seats.Count < room.PlayerCount) seats.Add(null);
        var matchId = await matches.StartMatchAsync(MatchMode.Private, seats, room.Code, room.Mode);
        lock (_gate)
        {
            room.MatchId = matchId;
            _rooms.Remove(room.Id);
            _played[matchId] = (room.PlayerCount, members, room.Mode);
        }
        await BroadcastAsync(room);
        return (matchId.ToString("N"), null);
    }

    /// <summary>Opens a new table with the same people after a private match. The requester becomes host.</summary>
    public async Task<(RoomInfo? Room, ServerError? Error)> RematchAsync(Guid user, string? matchId)
    {
        if (!Guid.TryParse(matchId, out var id)) return (null, new ServerError(ErrorCode.UnknownMatch, "Match not found."));
        (int PlayerCount, List<Guid> Members, GameMode Mode) played;
        Room? existing;
        lock (_gate)
        {
            // If someone already opened the rematch table (and seated us), return it.
            existing = _rooms.Values.FirstOrDefault(r => r.MatchId is null && r.Members.Contains(user));
            if (existing is null && (!_played.TryGetValue(id, out played) || !played.Members.Contains(user)))
                return (null, new ServerError(ErrorCode.UnknownMatch, "Rematch is only available for private tables."));
            played = existing is null ? _played[id] : default;
        }
        if (existing is not null) return (await InfoAsync(existing, user), null);

        var (info, error) = await CreateAsync(user, played.PlayerCount, played.Mode);
        if (info is null) return (null, error);
        Room room;
        lock (_gate)
        {
            room = _rooms[Guid.Parse(info.RoomId)];
            foreach (var m in played.Members.Where(m => m != user && matches.ActiveMatchFor(m) is null))
            {
                RemoveFromRoomsLocked(m);
                if (room.Members.Count < room.PlayerCount) room.Members.Add(m);
            }
            _played.Remove(id);
        }
        await BroadcastAsync(room);
        return (await InfoAsync(room, user), null);
    }

    public void Tick()
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromMinutes(options.Value.PrivateRoomIdleMinutes);
        lock (_gate)
        {
            foreach (var r in _rooms.Values.Where(r => r.LastActivity < cutoff && r.MatchId is null).ToList()) _rooms.Remove(r.Id);
            if (_played.Count > 5000) _played.Clear();
        }
    }

    public async Task OnDisconnectedAsync(Guid user)
    {
        string? roomId;
        lock (_gate) roomId = _rooms.Values.FirstOrDefault(r => r.Members.Contains(user) && r.MatchId is null)?.Id.ToString();
        if (roomId is not null) await LeaveAsync(user, roomId);
    }

    private void RemoveFromRoomsLocked(Guid user)
    {
        foreach (var r in _rooms.Values.Where(r => r.MatchId is null && r.Members.Contains(user)).ToList())
        {
            r.Members.Remove(user);
            if (r.Members.Count == 0) _rooms.Remove(r.Id);
            else if (r.Host == user) r.Host = r.Members[0];
        }
    }

    private async Task BroadcastAsync(Room room)
    {
        List<Guid> members;
        lock (_gate) members = room.Members.ToList();
        foreach (var m in members) await notifier.SendRoomUpdatedAsync(m, await InfoAsync(room, m));
    }

    private async Task<RoomInfo> InfoAsync(Room room, Guid viewer)
    {
        List<Guid> members;
        Guid host;
        Guid? matchId;
        lock (_gate) { members = room.Members.ToList(); host = room.Host; matchId = room.MatchId; }
        var map = await profiles.GetProfilesAsync(members);
        var colors = room.PlayerCount == 2
            ? new[] { PlayerColor.Ruby, PlayerColor.Topaz }
            : new[] { PlayerColor.Ruby, PlayerColor.Emerald, PlayerColor.Topaz, PlayerColor.Sapphire };
        var seats = members.Select((m, i) => new SeatInfo(i, colors[i], map.GetValueOrDefault(m)?.DisplayName ?? "Player", map.GetValueOrDefault(m)?.AvatarId ?? "crown", false, matches.IsConnected(m))).ToList();
        var started = matchId is not null && matchId != Guid.Empty;
        return new RoomInfo(room.Id.ToString(), room.Code, room.PlayerCount, seats, host == viewer, started, started ? matchId!.Value.ToString("N") : null, room.Mode);
    }
}
